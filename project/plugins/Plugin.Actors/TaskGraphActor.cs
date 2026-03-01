using Akka.Actor;
using GiantIsopod.Contracts.Core;
using Microsoft.Extensions.Logging;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// /user/taskgraph — owns task DAGs, validates acyclicity, dispatches ready nodes.
/// Currently uses an internal adjacency list; will migrate to ModernSatsuma once
/// the TopologicalSort algorithm is available (RFC-007).
/// </summary>
public sealed class TaskGraphActor : UntypedActor, IWithTimers
{
    private readonly IActorRef _dispatch;
    private readonly ILogger<TaskGraphActor> _logger;
    private readonly Dictionary<string, GraphState> _graphs = new();

    public ITimerScheduler Timers { get; set; } = null!;

    public TaskGraphActor(IActorRef dispatch, ILogger<TaskGraphActor> logger)
    {
        _dispatch = dispatch;
        _logger = logger;
    }

    protected override void OnReceive(object message)
    {
        switch (message)
        {
            case SubmitTaskGraph submit:
                HandleSubmit(submit);
                break;

            case TaskCompleted completed:
                HandleTaskCompleted(completed);
                break;

            case TaskFailed failed:
                HandleTaskFailed(failed);
                break;

            case GraphTimedOut timedOut:
                HandleGraphTimedOut(timedOut);
                break;
        }
    }

    private void HandleSubmit(SubmitTaskGraph submit)
    {
        var state = GraphState.TryCreate(submit, _logger);
        if (state is null)
        {
            Sender.Tell(new TaskGraphRejected(submit.GraphId, "Graph contains a cycle"));
            return;
        }

        _graphs[submit.GraphId] = state;
        Sender.Tell(new TaskGraphAccepted(submit.GraphId, submit.Nodes.Count, submit.Edges.Count));
        _logger.LogInformation("Task graph {GraphId} accepted: {Nodes} nodes, {Edges} edges",
            submit.GraphId, submit.Nodes.Count, submit.Edges.Count);

        // Schedule graph-level deadline if present
        if (submit.GraphBudget?.Deadline is { } deadline)
        {
            Timers.StartSingleTimer(
                $"deadline-{submit.GraphId}",
                new GraphTimedOut(submit.GraphId),
                deadline);
        }

        DispatchReadyNodes(state);
    }

    private void HandleTaskCompleted(TaskCompleted completed)
    {
        foreach (var (graphId, state) in _graphs)
        {
            if (!state.Nodes.ContainsKey(completed.TaskId)) continue;

            state.Status[completed.TaskId] = TaskNodeStatus.Completed;
            _logger.LogDebug("Graph {GraphId}: task {TaskId} completed", graphId, completed.TaskId);

            DispatchReadyNodes(state);
            CheckGraphCompletion(graphId, state);
            return;
        }
    }

    private void HandleTaskFailed(TaskFailed failed)
    {
        foreach (var (graphId, state) in _graphs)
        {
            if (!state.Nodes.ContainsKey(failed.TaskId)) continue;

            state.Status[failed.TaskId] = TaskNodeStatus.Failed;
            _logger.LogWarning("Graph {GraphId}: task {TaskId} failed — {Reason}",
                graphId, failed.TaskId, failed.Reason);

            // Cancel all transitive dependents
            CancelDependents(state, failed.TaskId);
            CheckGraphCompletion(graphId, state);
            return;
        }
    }

    private void DispatchReadyNodes(GraphState state)
    {
        foreach (var (taskId, node) in state.Nodes)
        {
            if (state.Status[taskId] != TaskNodeStatus.Pending) continue;

            // Check all incoming dependencies are completed
            var deps = state.IncomingEdges.GetValueOrDefault(taskId);
            if (deps is not null && deps.Any(d => state.Status[d] != TaskNodeStatus.Completed))
                continue;

            // All deps satisfied — mark ready and dispatch
            state.Status[taskId] = TaskNodeStatus.Dispatched;
            _logger.LogDebug("Graph {GraphId}: dispatching task {TaskId}", state.GraphId, taskId);

            var request = node.Budget is not null
                ? new TaskRequestWithBudget(taskId, node.Description, node.RequiredCapabilities, node.Budget)
                : new TaskRequest(taskId, node.Description, node.RequiredCapabilities);

            _dispatch.Tell(request);
        }
    }

    private void CancelDependents(GraphState state, string failedTaskId)
    {
        // BFS from failed node through outgoing edges, cancelling all reachable pending nodes
        var queue = new Queue<string>();
        queue.Enqueue(failedTaskId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!state.OutgoingEdges.TryGetValue(current, out var dependents)) continue;

            foreach (var dep in dependents)
            {
                if (state.Status[dep] is TaskNodeStatus.Pending or TaskNodeStatus.Ready)
                {
                    state.Status[dep] = TaskNodeStatus.Cancelled;
                    _logger.LogDebug("Graph {GraphId}: cancelled task {TaskId} (dependency {Dep} failed)",
                        state.GraphId, dep, failedTaskId);
                    queue.Enqueue(dep);
                }
            }
        }
    }

    private void HandleGraphTimedOut(GraphTimedOut timedOut)
    {
        if (!_graphs.TryGetValue(timedOut.GraphId, out var state))
            return;

        _logger.LogWarning("Graph {GraphId} deadline exceeded — aborting all pending tasks", timedOut.GraphId);

        // Cancel all non-terminal nodes
        foreach (var (taskId, status) in state.Status)
        {
            if (status is TaskNodeStatus.Pending or TaskNodeStatus.Ready or TaskNodeStatus.Dispatched)
            {
                state.Status[taskId] = status == TaskNodeStatus.Dispatched
                    ? TaskNodeStatus.Failed
                    : TaskNodeStatus.Cancelled;
            }
        }

        CheckGraphCompletion(timedOut.GraphId, state);
    }

    private void CheckGraphCompletion(string graphId, GraphState state)
    {
        var allTerminal = state.Status.Values.All(s =>
            s is TaskNodeStatus.Completed or TaskNodeStatus.Failed or TaskNodeStatus.Cancelled);

        if (!allTerminal) return;

        var results = state.Status.ToDictionary(
            kv => kv.Key,
            kv => kv.Value == TaskNodeStatus.Completed);

        Context.Parent.Tell(new TaskGraphCompleted(graphId, results));
        // Also publish on EventStream for any listeners
        Context.System.EventStream.Publish(new TaskGraphCompleted(graphId, results));

        _graphs.Remove(graphId);
        _logger.LogInformation("Graph {GraphId} completed: {Done}/{Total} succeeded",
            graphId,
            results.Values.Count(v => v),
            results.Count);
    }

    /// <summary>Internal state for a single task graph.</summary>
    internal sealed class GraphState
    {
        public required string GraphId { get; init; }
        public required Dictionary<string, TaskNode> Nodes { get; init; }
        public required Dictionary<string, List<string>> IncomingEdges { get; init; }
        public required Dictionary<string, List<string>> OutgoingEdges { get; init; }
        public required Dictionary<string, TaskNodeStatus> Status { get; init; }

        /// <summary>
        /// Builds graph state from a SubmitTaskGraph message.
        /// Returns null if the graph contains a cycle (Kahn's algorithm detects this).
        /// </summary>
        public static GraphState? TryCreate(SubmitTaskGraph submit, ILogger logger)
        {
            var nodes = submit.Nodes.ToDictionary(n => n.TaskId);
            var incoming = new Dictionary<string, List<string>>();
            var outgoing = new Dictionary<string, List<string>>();
            var inDegree = new Dictionary<string, int>();

            foreach (var node in submit.Nodes)
            {
                incoming[node.TaskId] = new List<string>();
                outgoing[node.TaskId] = new List<string>();
                inDegree[node.TaskId] = 0;
            }

            foreach (var edge in submit.Edges)
            {
                if (!nodes.ContainsKey(edge.FromTaskId) || !nodes.ContainsKey(edge.ToTaskId))
                {
                    logger.LogWarning("Graph {GraphId}: edge references unknown task ({From} → {To})",
                        submit.GraphId, edge.FromTaskId, edge.ToTaskId);
                    continue;
                }

                outgoing[edge.FromTaskId].Add(edge.ToTaskId);
                incoming[edge.ToTaskId].Add(edge.FromTaskId);
                inDegree[edge.ToTaskId]++;
            }

            // Kahn's algorithm for cycle detection
            var queue = new Queue<string>();
            foreach (var (taskId, degree) in inDegree)
            {
                if (degree == 0) queue.Enqueue(taskId);
            }

            var visited = 0;
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                visited++;
                foreach (var dep in outgoing[current])
                {
                    inDegree[dep]--;
                    if (inDegree[dep] == 0) queue.Enqueue(dep);
                }
            }

            if (visited != nodes.Count)
            {
                var cyclicNodes = inDegree.Where(kv => kv.Value > 0).Select(kv => kv.Key);
                logger.LogWarning("Graph {GraphId} rejected: cycle detected involving [{Nodes}]",
                    submit.GraphId, string.Join(", ", cyclicNodes));
                return null;
            }

            var status = nodes.Keys.ToDictionary(id => id, _ => TaskNodeStatus.Pending);

            return new GraphState
            {
                GraphId = submit.GraphId,
                Nodes = nodes,
                IncomingEdges = incoming,
                OutgoingEdges = outgoing,
                Status = status,
            };
        }
    }

    private record GraphTimedOut(string GraphId);
}
