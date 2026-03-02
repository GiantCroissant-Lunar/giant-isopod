using Akka.Actor;
using GiantIsopod.Contracts.Core;
using Microsoft.Extensions.Logging;
using Plate.ModernSatsuma;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// /user/taskgraph — owns task DAGs, validates acyclicity, dispatches ready nodes.
/// Uses Plate.ModernSatsuma TopologicalSort (RFC-007) for cycle detection.
/// Supports progressive decomposition (ADR-009): agents can propose subtask DAGs
/// at runtime, which are validated and inserted into the live graph.
/// </summary>
public sealed class TaskGraphActor : UntypedActor, IWithTimers
{
    private readonly IActorRef _dispatch;
    private readonly IActorRef _agentSupervisor;
    private readonly IActorRef _viewport;
    private readonly ILogger<TaskGraphActor> _logger;
    private readonly Dictionary<string, GraphState> _graphs = new();

    public ITimerScheduler Timers { get; set; } = null!;

    public TaskGraphActor(IActorRef dispatch, IActorRef agentSupervisor, IActorRef viewport, ILogger<TaskGraphActor> logger)
    {
        _dispatch = dispatch;
        _agentSupervisor = agentSupervisor;
        _viewport = viewport;
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
        _viewport.Tell(new NotifyTaskGraphSubmitted(submit.GraphId, submit.Nodes, submit.Edges));
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
        if (!TryFindGraph(completed.TaskId, completed.GraphId, out var graphId, out var state))
            return;

        // Track which agent completed this task
        state.AssignedAgent[completed.TaskId] = completed.AgentId;

        if (!completed.Success)
        {
            state.Status[completed.TaskId] = TaskNodeStatus.Failed;
            _viewport.Tell(new NotifyTaskNodeStatusChanged(graphId, completed.TaskId, TaskNodeStatus.Failed, completed.AgentId));
            _logger.LogWarning("Graph {GraphId}: task {TaskId} completed with Success=false",
                graphId, completed.TaskId);
            CancelDependents(state, completed.TaskId);
        }
        else if (completed.Subplan != null)
        {
            // Agent proposed a decomposition — validate and insert subtasks
            HandleDecomposition(graphId, state, completed);
        }
        else
        {
            state.Status[completed.TaskId] = TaskNodeStatus.Completed;
            state.CompletedResults[completed.TaskId] = completed;
            _viewport.Tell(new NotifyTaskNodeStatusChanged(graphId, completed.TaskId, TaskNodeStatus.Completed, completed.AgentId));
            _logger.LogDebug("Graph {GraphId}: task {TaskId} completed", graphId, completed.TaskId);
            DispatchReadyNodes(state);
            CheckSubtasksCompleted(graphId, state, completed.TaskId);
        }

        CheckGraphCompletion(graphId, state);
    }

    private void HandleDecomposition(string graphId, GraphState state, TaskCompleted completed)
    {
        var subplan = completed.Subplan!;
        var parentId = completed.TaskId;
        var parentDepth = state.Depth.GetValueOrDefault(parentId, 0);

        // Validate subtask count
        if (subplan.Subtasks.Count > MaxSubtasksPerDecomposition)
        {
            RejectDecomposition(graphId, state, completed,
                $"Too many subtasks ({subplan.Subtasks.Count} > {MaxSubtasksPerDecomposition})");
            return;
        }

        // Validate depth
        if (parentDepth + 1 > MaxDepth)
        {
            RejectDecomposition(graphId, state, completed,
                $"Maximum decomposition depth exceeded ({parentDepth + 1} > {MaxDepth})");
            return;
        }

        // Validate total node count
        if (state.Nodes.Count + subplan.Subtasks.Count > MaxTotalNodes)
        {
            RejectDecomposition(graphId, state, completed,
                $"Maximum total nodes exceeded ({state.Nodes.Count + subplan.Subtasks.Count} > {MaxTotalNodes})");
            return;
        }

        // Generate subtask IDs
        var subtaskIds = new List<string>();
        for (var i = 0; i < subplan.Subtasks.Count; i++)
            subtaskIds.Add($"{parentId}/sub-{i}");

        // Build subtask dependency graph and validate no cycles
        var subGraph = new CustomGraph();
        var subNodeMap = new Dictionary<int, Node>();
        for (var i = 0; i < subplan.Subtasks.Count; i++)
            subNodeMap[i] = subGraph.AddNode();

        for (var i = 0; i < subplan.Subtasks.Count; i++)
        {
            foreach (var depRef in subplan.Subtasks[i].DependsOnSubtasks)
            {
                if (!int.TryParse(depRef, out var depIdx) || depIdx < 0 || depIdx >= subplan.Subtasks.Count)
                {
                    RejectDecomposition(graphId, state, completed,
                        $"Subtask {i} references invalid dependency index '{depRef}'");
                    return;
                }
                subGraph.AddArc(subNodeMap[depIdx], subNodeMap[i], Directedness.Directed);
            }
        }

        var topoSort = new Plate.ModernSatsuma.TopologicalSort(subGraph);
        if (!topoSort.IsAcyclic)
        {
            RejectDecomposition(graphId, state, completed,
                "Subtask dependencies contain a cycle");
            return;
        }

        // Insert subtasks into graph state
        var childDepth = parentDepth + 1;
        state.ChildTaskIds[parentId] = subtaskIds;
        state.StopConditions[parentId] = subplan.StopWhen;

        for (var i = 0; i < subplan.Subtasks.Count; i++)
        {
            var proposal = subplan.Subtasks[i];
            var subtaskId = subtaskIds[i];

            var node = new TaskNode(subtaskId, proposal.Description, proposal.RequiredCapabilities,
                proposal.BudgetCap != null ? new TaskBudget(Deadline: proposal.BudgetCap) : null);

            state.Nodes[subtaskId] = node;
            state.Status[subtaskId] = TaskNodeStatus.Pending;
            state.Depth[subtaskId] = childDepth;
            state.ParentTaskId[subtaskId] = parentId;
            state.IncomingEdges[subtaskId] = new List<string>();
            state.OutgoingEdges[subtaskId] = new List<string>();

            // Wire up intra-subtask dependencies
            foreach (var depRef in proposal.DependsOnSubtasks)
            {
                var depIdx = int.Parse(depRef);
                var depId = subtaskIds[depIdx];
                state.IncomingEdges[subtaskId].Add(depId);
                state.OutgoingEdges[depId].Add(subtaskId);
            }
        }

        // Set parent to WaitingForSubtasks
        state.Status[parentId] = TaskNodeStatus.WaitingForSubtasks;
        _viewport.Tell(new NotifyTaskNodeStatusChanged(graphId, parentId, TaskNodeStatus.WaitingForSubtasks, completed.AgentId));

        _logger.LogInformation("Graph {GraphId}: task {TaskId} decomposed into {Count} subtasks (depth={Depth})",
            graphId, parentId, subplan.Subtasks.Count, childDepth);

        // Notify the agent
        _agentSupervisor.Tell(new ForwardToAgent(
            completed.AgentId,
            new TaskDecompositionAccepted(parentId, subtaskIds, graphId)));

        // Dispatch ready subtasks
        DispatchReadyNodes(state);
    }

    private void RejectDecomposition(string graphId, GraphState state, TaskCompleted completed, string reason)
    {
        _logger.LogWarning("Graph {GraphId}: decomposition rejected for task {TaskId}: {Reason}",
            graphId, completed.TaskId, reason);

        // Leave parent as Dispatched so it can complete normally
        _agentSupervisor.Tell(new ForwardToAgent(
            completed.AgentId,
            new TaskDecompositionRejected(completed.TaskId, reason, graphId)));
    }

    private void CheckSubtasksCompleted(string graphId, GraphState state, string completedTaskId)
    {
        // Find parent of completed task
        if (!state.ParentTaskId.TryGetValue(completedTaskId, out var parentId) || parentId == null)
            return;

        if (state.Status.GetValueOrDefault(parentId) != TaskNodeStatus.WaitingForSubtasks)
            return;

        if (!state.ChildTaskIds.TryGetValue(parentId, out var childIds))
            return;

        var stopCondition = state.StopConditions.GetValueOrDefault(parentId);
        var stopKind = stopCondition?.Kind ?? StopKind.AllSubtasksComplete;

        if (stopKind == StopKind.FirstSuccess)
        {
            // Check if any child succeeded
            var firstSuccess = childIds.FirstOrDefault(id =>
                state.Status.GetValueOrDefault(id) == TaskNodeStatus.Completed);

            if (firstSuccess != null)
            {
                // Cancel remaining non-terminal siblings
                foreach (var siblingId in childIds)
                {
                    var siblingStatus = state.Status.GetValueOrDefault(siblingId);
                    if (siblingStatus is TaskNodeStatus.Pending or TaskNodeStatus.Ready or TaskNodeStatus.Dispatched)
                    {
                        state.Status[siblingId] = siblingStatus == TaskNodeStatus.Dispatched
                            ? TaskNodeStatus.Failed
                            : TaskNodeStatus.Cancelled;
                        _viewport.Tell(new NotifyTaskNodeStatusChanged(graphId, siblingId, state.Status[siblingId]));
                    }
                }

                TriggerSynthesis(graphId, state, parentId);
                return;
            }
        }

        // AllSubtasksComplete (default): all children must be terminal
        var allTerminal = childIds.All(id =>
            state.Status.GetValueOrDefault(id) is TaskNodeStatus.Completed or TaskNodeStatus.Failed or TaskNodeStatus.Cancelled);

        if (allTerminal)
        {
            TriggerSynthesis(graphId, state, parentId);
        }
    }

    private void TriggerSynthesis(string graphId, GraphState state, string parentId)
    {
        state.Status[parentId] = TaskNodeStatus.Synthesizing;
        _viewport.Tell(new NotifyTaskNodeStatusChanged(graphId, parentId, TaskNodeStatus.Synthesizing));

        // Collect completed child results
        var childIds = state.ChildTaskIds[parentId];
        var childResults = childIds
            .Where(id => state.CompletedResults.ContainsKey(id))
            .Select(id => state.CompletedResults[id])
            .ToList();

        // Find the agent that owns the parent task
        if (!state.AssignedAgent.TryGetValue(parentId, out var agentId))
        {
            _logger.LogWarning("Graph {GraphId}: no agent assigned for parent task {TaskId}, cannot synthesize",
                graphId, parentId);
            return;
        }

        _logger.LogInformation("Graph {GraphId}: triggering synthesis for task {TaskId} ({Count} child results)",
            graphId, parentId, childResults.Count);

        _agentSupervisor.Tell(new ForwardToAgent(
            agentId,
            new SubtasksCompleted(parentId, childResults, graphId)));
    }

    private void HandleTaskFailed(TaskFailed failed)
    {
        if (!TryFindGraph(failed.TaskId, failed.GraphId, out var graphId, out var state))
            return;

        state.Status[failed.TaskId] = TaskNodeStatus.Failed;
        _viewport.Tell(new NotifyTaskNodeStatusChanged(graphId, failed.TaskId, TaskNodeStatus.Failed));
        _logger.LogWarning("Graph {GraphId}: task {TaskId} failed — {Reason}",
            graphId, failed.TaskId, failed.Reason);

        CancelDependents(state, failed.TaskId);
        CheckSubtasksCompleted(graphId, state, failed.TaskId);
        CheckGraphCompletion(graphId, state);
    }

    /// <summary>
    /// Resolves the owning graph for a task. Uses direct GraphId lookup when available,
    /// falls back to scanning all graphs for backward compatibility.
    /// </summary>
    private bool TryFindGraph(string taskId, string? graphId, out string foundGraphId, out GraphState state)
    {
        // Direct lookup when GraphId is provided — O(1) instead of O(graphs)
        if (graphId != null && _graphs.TryGetValue(graphId, out state!))
        {
            if (state.Nodes.ContainsKey(taskId))
            {
                foundGraphId = graphId;
                return true;
            }
            _logger.LogWarning("GraphId {GraphId} does not contain task {TaskId}", graphId, taskId);
        }

        // Fallback: scan all graphs (backward compat for messages without GraphId)
        foreach (var (gid, gs) in _graphs)
        {
            if (!gs.Nodes.ContainsKey(taskId)) continue;
            foundGraphId = gid;
            state = gs;
            return true;
        }

        foundGraphId = "";
        state = null!;
        return false;
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
            _viewport.Tell(new NotifyTaskNodeStatusChanged(state.GraphId, taskId, TaskNodeStatus.Dispatched));
            _logger.LogDebug("Graph {GraphId}: dispatching task {TaskId}", state.GraphId, taskId);

            var request = node.Budget is not null
                ? new TaskRequestWithBudget(taskId, node.Description, node.RequiredCapabilities, node.Budget, state.GraphId)
                : new TaskRequest(taskId, node.Description, node.RequiredCapabilities, state.GraphId);

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
                    _viewport.Tell(new NotifyTaskNodeStatusChanged(state.GraphId, dep, TaskNodeStatus.Cancelled));
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
            if (status is TaskNodeStatus.Pending or TaskNodeStatus.Ready or TaskNodeStatus.Dispatched
                or TaskNodeStatus.WaitingForSubtasks or TaskNodeStatus.Synthesizing)
            {
                state.Status[taskId] = status is TaskNodeStatus.Dispatched
                    or TaskNodeStatus.WaitingForSubtasks or TaskNodeStatus.Synthesizing
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
        _viewport.Tell(new TaskGraphCompleted(graphId, results));
        Context.Parent.Tell(new TaskGraphCompleted(graphId, results));
        Context.System.EventStream.Publish(new TaskGraphCompleted(graphId, results));

        _graphs.Remove(graphId);
        _logger.LogInformation("Graph {GraphId} completed: {Done}/{Total} succeeded",
            graphId,
            results.Values.Count(v => v),
            results.Count);
    }

    // ── Constants ──

    internal const int MaxDepth = 3;
    internal const int MaxSubtasksPerDecomposition = 10;
    internal const int MaxTotalNodes = 100;

    /// <summary>Internal state for a single task graph.</summary>
    internal sealed class GraphState
    {
        public required string GraphId { get; init; }
        public required Dictionary<string, TaskNode> Nodes { get; init; }
        public required Dictionary<string, List<string>> IncomingEdges { get; init; }
        public required Dictionary<string, List<string>> OutgoingEdges { get; init; }
        public required Dictionary<string, TaskNodeStatus> Status { get; init; }
        public required Dictionary<string, int> Depth { get; init; }
        public required Dictionary<string, string?> ParentTaskId { get; init; }
        public Dictionary<string, List<string>> ChildTaskIds { get; } = new();
        public Dictionary<string, StopCondition?> StopConditions { get; } = new();
        public Dictionary<string, string> AssignedAgent { get; } = new();
        public Dictionary<string, TaskCompleted> CompletedResults { get; } = new();

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
            var depth = nodes.Keys.ToDictionary(id => id, _ => 0);
            var parentTaskId = nodes.Keys.ToDictionary(id => id, _ => (string?)null);

            return (new GraphState
            {
                GraphId = submit.GraphId,
                Nodes = nodes,
                IncomingEdges = incoming,
                OutgoingEdges = outgoing,
                Status = status,
                Depth = depth,
                ParentTaskId = parentTaskId,
            }, null);
        }
    }

    private record GraphTimedOut(string GraphId);
}
