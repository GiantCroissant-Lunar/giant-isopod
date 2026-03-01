using Akka.Actor;
using GiantIsopod.Contracts.Core;
using Microsoft.Extensions.Logging;
using Plate.ModernSatsuma;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// /user/taskgraph — owns task DAGs, validates acyclicity, dispatches ready nodes.
/// Uses Plate.ModernSatsuma TopologicalSort (RFC-007) for cycle detection.
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
        var (state, rejectReason) = GraphState.TryCreate(submit, _logger);
        if (state is null)
        {
            Sender.Tell(new TaskGraphRejected(submit.GraphId, rejectReason!));
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

            if (!completed.Success)
            {
                // Treat unsuccessful completion as failure
                state.Status[completed.TaskId] = TaskNodeStatus.Failed;
                _logger.LogWarning("Graph {GraphId}: task {TaskId} completed with Success=false",
                    graphId, completed.TaskId);
                CancelDependents(state, completed.TaskId);
            }
            else
            {
                state.Status[completed.TaskId] = TaskNodeStatus.Completed;
                _logger.LogDebug("Graph {GraphId}: task {TaskId} completed", graphId, completed.TaskId);
                DispatchReadyNodes(state);
            }

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

        // Cancel all non-terminal nodes (snapshot keys to avoid mutation during iteration)
        foreach (var taskId in state.Status.Keys.ToList())
        {
            var status = state.Status[taskId];
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

        Timers.Cancel($"deadline-{graphId}");
        Context.Parent.Tell(new TaskGraphCompleted(graphId, results));
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
        /// Uses Plate.ModernSatsuma TopologicalSort for cycle detection (RFC-007).
        /// Returns (null, reason) if the graph is invalid.
        /// </summary>
        public static (GraphState? State, string? RejectReason) TryCreate(SubmitTaskGraph submit, ILogger logger)
        {
            // Detect duplicate TaskIds before building (ToDictionary would throw)
            var duplicates = submit.Nodes.GroupBy(n => n.TaskId).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (duplicates.Count > 0)
            {
                logger.LogWarning("Graph {GraphId} rejected: duplicate TaskIds [{Ids}]",
                    submit.GraphId, string.Join(", ", duplicates));
                return (null, $"Duplicate TaskIds: {string.Join(", ", duplicates)}");
            }

            var nodes = submit.Nodes.ToDictionary(n => n.TaskId);
            var incoming = new Dictionary<string, List<string>>();
            var outgoing = new Dictionary<string, List<string>>();

            // Build ModernSatsuma graph for cycle detection
            var graph = new CustomGraph();
            var nodeMap = new Dictionary<string, Node>();

            foreach (var node in submit.Nodes)
            {
                incoming[node.TaskId] = new List<string>();
                outgoing[node.TaskId] = new List<string>();
                nodeMap[node.TaskId] = graph.AddNode();
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
                graph.AddArc(nodeMap[edge.FromTaskId], nodeMap[edge.ToTaskId], Directedness.Directed);
            }

            // Use ModernSatsuma TopologicalSort for cycle detection
            var topoSort = new Plate.ModernSatsuma.TopologicalSort(graph);
            if (!topoSort.IsAcyclic)
            {
                // Map cyclic ModernSatsuma nodes back to task IDs
                var reverseMap = nodeMap.ToDictionary(kv => kv.Value.Id, kv => kv.Key);
                var cyclicTaskIds = topoSort.CyclicNodes
                    .Where(n => reverseMap.ContainsKey(n.Id))
                    .Select(n => reverseMap[n.Id]);
                logger.LogWarning("Graph {GraphId} rejected: cycle detected involving [{Nodes}]",
                    submit.GraphId, string.Join(", ", cyclicTaskIds));
                return (null, $"Graph contains a cycle involving: {string.Join(", ", cyclicTaskIds)}");
            }

            var status = nodes.Keys.ToDictionary(id => id, _ => TaskNodeStatus.Pending);

            return (new GraphState
            {
                GraphId = submit.GraphId,
                Nodes = nodes,
                IncomingEdges = incoming,
                OutgoingEdges = outgoing,
                Status = status,
            }, null);
        }
    }

    private record GraphTimedOut(string GraphId);
}
