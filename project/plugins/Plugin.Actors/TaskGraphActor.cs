using Akka.Actor;
using GiantIsopod.Contracts.Core;
using Microsoft.Extensions.Logging;
using Plate.ModernSatsuma;
using System.Text;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// /user/taskgraph — owns task DAGs, validates acyclicity, dispatches ready nodes.
/// Uses Plate.ModernSatsuma TopologicalSort (RFC-007) for cycle detection.
/// Supports progressive decomposition (ADR-009): agents can propose subtask DAGs
/// at runtime, which are validated and inserted into the live graph.
/// </summary>
public sealed class TaskGraphActor : UntypedActor, IWithTimers
{
    private const string PlannerTaskSuffix = ".__plan";
    private readonly IActorRef _dispatch;
    private readonly IActorRef _agentSupervisor;
    private readonly IActorRef _viewport;
    private readonly IActorRef _workspace;
    private readonly IActorRef _validator;
    private readonly IActorRef _knowledgeSupervisor;
    private readonly ITaskGraphCheckpointStore _checkpointStore;
    private readonly ILogger<TaskGraphActor> _logger;
    private readonly Dictionary<string, GraphState> _graphs = new();
    private const string PlannerKnowledgeAgentId = "task-planner";

    public ITimerScheduler Timers { get; set; } = null!;

    public TaskGraphActor(IActorRef dispatch, IActorRef agentSupervisor, IActorRef viewport, IActorRef workspace, IActorRef validator, ILogger<TaskGraphActor> logger)
        : this(dispatch, agentSupervisor, viewport, workspace, validator, ActorRefs.Nobody, NullTaskGraphCheckpointStore.Instance, logger)
    {
    }

    public TaskGraphActor(
        IActorRef dispatch,
        IActorRef agentSupervisor,
        IActorRef viewport,
        IActorRef workspace,
        IActorRef validator,
        IActorRef knowledgeSupervisor,
        ILogger<TaskGraphActor> logger)
        : this(dispatch, agentSupervisor, viewport, workspace, validator, knowledgeSupervisor, NullTaskGraphCheckpointStore.Instance, logger)
    {
    }

    public TaskGraphActor(
        IActorRef dispatch,
        IActorRef agentSupervisor,
        IActorRef viewport,
        IActorRef workspace,
        IActorRef validator,
        IActorRef knowledgeSupervisor,
        ITaskGraphCheckpointStore checkpointStore,
        ILogger<TaskGraphActor> logger)
    {
        _dispatch = dispatch;
        _agentSupervisor = agentSupervisor;
        _viewport = viewport;
        _workspace = workspace;
        _validator = validator;
        _knowledgeSupervisor = knowledgeSupervisor;
        _checkpointStore = checkpointStore;
        _logger = logger;
    }

    protected override void PreStart()
    {
        RestoreCheckpoints();
        base.PreStart();
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

            case MergeSucceeded merged:
                HandleMergeSucceeded(merged);
                break;

            case MergeConflict conflict:
                HandleMergeConflict(conflict);
                break;

            case WorkspaceReleased released:
                HandleWorkspaceReleased(released);
                break;

            case ValidationComplete validation:
                HandleValidationComplete(validation);
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
        _viewport.Tell(new NotifyTaskGraphSubmitted(submit.GraphId, submit.Nodes, submit.Edges));

        // Schedule graph-level deadline if present
        if (submit.GraphBudget?.Deadline is { } deadline)
        {
            Timers.StartSingleTimer(
                $"deadline-{submit.GraphId}",
                new GraphTimedOut(submit.GraphId),
                deadline);
        }

        DispatchReadyNodes(state);
        PersistGraphState(state);
        Sender.Tell(new TaskGraphAccepted(submit.GraphId, submit.Nodes.Count, submit.Edges.Count));
        _logger.LogInformation("Task graph {GraphId} accepted: {Nodes} nodes, {Edges} edges",
            submit.GraphId, submit.Nodes.Count, submit.Edges.Count);
    }

    private void HandleTaskCompleted(TaskCompleted completed)
    {
        if (TryHandlePlannerTaskCompleted(completed))
            return;

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
        else if (completed.Artifacts is { Count: > 0 })
        {
            // Check if validation is needed
            var node = state.Nodes.GetValueOrDefault(completed.TaskId);
            var requiredValidators = node?.RequiredValidators;

            // Gate on validation before merge/completion
            state.PendingValidation[completed.TaskId] = new PendingValidationEntry(
                completed, completed.Artifacts.Count, new List<ValidatorResult>());
            state.Status[completed.TaskId] = TaskNodeStatus.Validating;
            _viewport.Tell(new NotifyTaskNodeStatusChanged(graphId, completed.TaskId, TaskNodeStatus.Validating, completed.AgentId));
            _viewport.Tell(new RuntimeOutput(
                completed.AgentId,
                $"[validator] Task {completed.TaskId} entered validation with {completed.Artifacts.Count} artifact(s)."));
            _logger.LogDebug("Graph {GraphId}: task {TaskId} has {Count} artifacts, sending to validation",
                graphId, completed.TaskId, completed.Artifacts.Count);

            foreach (var artifact in completed.Artifacts)
            {
                _validator.Tell(new ValidateArtifact(
                    artifact.ArtifactId, artifact,
                    completed.TaskId,
                    requiredValidators,
                    node?.OwnedPaths,
                    node?.ExpectedFiles));
            }
            PersistGraphState(state);
            return; // Wait for ValidationComplete messages
        }
        else
        {
            CompleteTaskNode(graphId, state, completed);
            RequestWorkspaceRelease(state, completed.TaskId);
        }

        CheckGraphCompletion(graphId, state);
        PersistGraphStateIfActive(graphId, state);
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

        // Pre-parse and validate all dependency indices; store parsed values for reuse
        var parsedDeps = new List<List<int>>();
        for (var i = 0; i < subplan.Subtasks.Count; i++)
        {
            var deps = new List<int>();
            foreach (var depRef in subplan.Subtasks[i].DependsOnSubtasks)
            {
                if (!int.TryParse(depRef, out var depIdx) || depIdx < 0 || depIdx >= subplan.Subtasks.Count)
                {
                    RejectDecomposition(graphId, state, completed,
                        $"Subtask {i} references invalid dependency index '{depRef}'");
                    return;
                }
                deps.Add(depIdx);
                subGraph.AddArc(subNodeMap[depIdx], subNodeMap[i], Directedness.Directed);
            }
            parsedDeps.Add(deps);
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

            if (proposal.OwnedPaths is not { Count: > 0 })
            {
                RejectDecomposition(graphId, state, completed,
                    $"Subtask {i} must declare owned_paths.");
                return;
            }

            if (proposal.ExpectedFiles is not { Count: > 0 })
            {
                RejectDecomposition(graphId, state, completed,
                    $"Subtask {i} must declare expected_files.");
                return;
            }

            var node = new TaskNode(
                subtaskId,
                proposal.Description,
                proposal.RequiredCapabilities,
                proposal.BudgetCap != null ? new TaskBudget(Deadline: proposal.BudgetCap) : null,
                OwnedPaths: proposal.OwnedPaths,
                ExpectedFiles: proposal.ExpectedFiles,
                AllowNoOpCompletion: proposal.AllowNoOpCompletion);

            state.Nodes[subtaskId] = node;
            state.Status[subtaskId] = TaskNodeStatus.Pending;
            state.Depth[subtaskId] = childDepth;
            state.ParentTaskId[subtaskId] = parentId;
            state.IncomingEdges[subtaskId] = new List<string>();
            state.OutgoingEdges[subtaskId] = new List<string>();

            // Wire up intra-subtask dependencies (using pre-parsed indices)
            foreach (var depIdx in parsedDeps[i])
            {
                var depId = subtaskIds[depIdx];
                state.IncomingEdges[subtaskId].Add(depId);
                state.OutgoingEdges[depId].Add(subtaskId);
            }
        }

        // Set parent to WaitingForSubtasks
        GraphState.AddImplicitOwnershipEdges(state.Nodes, state.IncomingEdges, state.OutgoingEdges, subtaskIds, _logger);

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
        PersistGraphState(state);
    }

    private void RejectDecomposition(string graphId, GraphState state, TaskCompleted completed, string reason)
    {
        _logger.LogWarning("Graph {GraphId}: decomposition rejected for task {TaskId}: {Reason}",
            graphId, completed.TaskId, reason);
        StorePlanningFeedback(
            "planning-pitfall",
            $"Planning pitfall for graph {graphId}, task {completed.TaskId}: decomposition rejected. Reason={reason}. Re-plan with explicit owned_paths and expected_files per subtask.",
            new Dictionary<string, string>
            {
                ["kind"] = "decomposition_rejected",
                ["graphId"] = graphId,
                ["taskId"] = completed.TaskId
            });

        // Leave parent as Dispatched so it can complete normally
        _agentSupervisor.Tell(new ForwardToAgent(
            completed.AgentId,
            new TaskDecompositionRejected(completed.TaskId, reason, graphId)));
        PersistGraphState(state);
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
            _logger.LogWarning("Graph {GraphId}: no agent assigned for parent task {TaskId}, failing",
                graphId, parentId);
            state.Status[parentId] = TaskNodeStatus.Failed;
            _viewport.Tell(new NotifyTaskNodeStatusChanged(graphId, parentId, TaskNodeStatus.Failed));
            return;
        }

        _logger.LogInformation("Graph {GraphId}: triggering synthesis for task {TaskId} ({Count} child results)",
            graphId, parentId, childResults.Count);

        _agentSupervisor.Tell(new ForwardToAgent(
            agentId,
            new SubtasksCompleted(parentId, childResults, graphId)));
    }

    private void CompleteTaskNode(string graphId, GraphState state, TaskCompleted completed)
    {
        state.Status[completed.TaskId] = TaskNodeStatus.Completed;
        state.CompletedResults[completed.TaskId] = completed;
        _viewport.Tell(new NotifyTaskNodeStatusChanged(graphId, completed.TaskId, TaskNodeStatus.Completed, completed.AgentId));
        _logger.LogDebug("Graph {GraphId}: task {TaskId} completed", graphId, completed.TaskId);
        DispatchReadyNodes(state);
        CheckSubtasksCompleted(graphId, state, completed.TaskId);
    }

    private void HandleMergeSucceeded(MergeSucceeded merged)
    {
        if (!TryFindGraphByTask(merged.TaskId, out var graphId, out var state))
            return;

        if (!state.PendingMerge.Remove(merged.TaskId, out var completed))
            return;

        _logger.LogInformation("Graph {GraphId}: task {TaskId} merged (sha: {Sha})", graphId, merged.TaskId, merged.MergeCommitSha);
        CompleteTaskNode(graphId, state, completed);
        RequestWorkspaceRelease(state, merged.TaskId);
        CheckGraphCompletion(graphId, state);
        PersistGraphStateIfActive(graphId, state);
    }

    private void HandleMergeConflict(MergeConflict conflict)
    {
        if (!TryFindGraphByTask(conflict.TaskId, out var graphId, out var state))
            return;

        state.PendingMerge.Remove(conflict.TaskId);

        _logger.LogWarning("Graph {GraphId}: task {TaskId} merge conflict on [{Files}]",
            graphId, conflict.TaskId, string.Join(", ", conflict.ConflictingFiles));
        StorePlanningFeedback(
            "planning-pitfall",
            BuildMergeConflictFeedback(graphId, state, conflict),
            new Dictionary<string, string>
            {
                ["kind"] = "merge_conflict",
                ["graphId"] = graphId,
                ["taskId"] = conflict.TaskId
            });

        // Fail the task — conflict resolution subtask is future work
        state.Status[conflict.TaskId] = TaskNodeStatus.Failed;
        _viewport.Tell(new NotifyTaskNodeStatusChanged(graphId, conflict.TaskId, TaskNodeStatus.Failed));
        CancelDependents(state, conflict.TaskId);
        RequestWorkspaceRelease(state, conflict.TaskId);
        CheckGraphCompletion(graphId, state);
        PersistGraphStateIfActive(graphId, state);
    }

    private void HandleValidationComplete(ValidationComplete validation)
    {
        string? taskId = null;
        string? graphId = null;
        GraphState? state = null;

        // Fast path: use TaskId from the message to avoid scanning all graphs
        if (validation.TaskId is { } hintedTaskId &&
            TryFindGraphByTask(hintedTaskId, out var hintedGraphId, out var hintedState) &&
            hintedState.PendingValidation.ContainsKey(hintedTaskId))
        {
            taskId = hintedTaskId;
            graphId = hintedGraphId;
            state = hintedState;
        }
        else
        {
            // Fallback: scan all graphs for the artifact (backward compat)
            foreach (var (gid, gs) in _graphs)
            {
                foreach (var (tid, pv) in gs.PendingValidation)
                {
                    if (pv.Completed.Artifacts?.Any(a => a.ArtifactId == validation.ArtifactId) == true)
                    {
                        taskId = tid;
                        graphId = gid;
                        state = gs;
                        break;
                    }
                }
                if (taskId != null) break;
            }
        }

        if (taskId == null || graphId == null || state == null)
        {
            _logger.LogWarning("ValidationComplete for unknown artifact {ArtifactId}", validation.ArtifactId);
            return;
        }

        if (!state.PendingValidation.TryGetValue(taskId, out var pending))
        {
            _logger.LogWarning("ValidationComplete for task {TaskId} with no pending validation entry", taskId);
            return;
        }
        pending.AllResults.AddRange(validation.Results);
        pending.RemainingArtifacts--;

        if (pending.RemainingArtifacts > 0)
        {
            PersistGraphState(state);
            return; // Still waiting for more artifacts
        }

        // All artifacts validated — check results
        state.PendingValidation.Remove(taskId);
        var failures = pending.AllResults.Where(r => !r.Passed).ToList();

        if (failures.Count == 0)
        {
            _logger.LogDebug("Graph {GraphId}: task {TaskId} passed all validation", graphId, taskId);
            if (state.AssignedAgent.TryGetValue(taskId, out var assignedAgentId))
            {
                _viewport.Tell(new RuntimeOutput(
                    assignedAgentId,
                    $"[validator] Task {taskId} passed validation."));
            }

            // Proceed to merge (if code artifacts) or complete
            var hasCodeArtifacts = pending.Completed.Artifacts?.Any(a => a.Type == ArtifactType.Code) == true;
            if (hasCodeArtifacts)
            {
                state.PendingMerge[taskId] = pending.Completed;
                _logger.LogDebug("Graph {GraphId}: task {TaskId} has code artifacts, requesting merge", graphId, taskId);
                _workspace.Tell(new RequestMerge(taskId));
            }
            else
            {
                CompleteTaskNode(graphId, state, pending.Completed);
                RequestWorkspaceRelease(state, taskId);
                CheckGraphCompletion(graphId, state);
                PersistGraphStateIfActive(graphId, state);
            }
        }
        else
        {
            // Validation failed — check retry budget
            var attempts = state.ValidationAttempts.GetValueOrDefault(taskId, 0) + 1;
            state.ValidationAttempts[taskId] = attempts;

            var node = state.Nodes.GetValueOrDefault(taskId);
            var maxAttempts = node?.MaxValidationAttempts ?? 2;
            var failureSummary = string.Join("; ", failures.Select(f => $"{f.ValidatorName}: {f.Details}"));

            if (attempts < maxAttempts)
            {
                _logger.LogWarning("Graph {GraphId}: task {TaskId} failed validation (attempt {Attempt}/{Max}), re-dispatching",
                    graphId, taskId, attempts, maxAttempts);
                if (state.AssignedAgent.TryGetValue(taskId, out var assignedAgentId))
                {
                    _viewport.Tell(new RuntimeOutput(
                        assignedAgentId,
                        $"[validator] Task {taskId} failed validation (attempt {attempts}/{maxAttempts}): {failureSummary}"));
                }

                // Re-dispatch with failure feedback
                state.Status[taskId] = TaskNodeStatus.Dispatched;
                _viewport.Tell(new NotifyTaskNodeStatusChanged(graphId, taskId, TaskNodeStatus.Dispatched));

                var revisedDesc = $"{node?.Description ?? "task"} [REVISION {attempts}: validation failed — {failureSummary}]";

                TaskRequest request = node?.Budget != null
                    ? new TaskRequestWithBudget(
                        taskId,
                        revisedDesc,
                        node.RequiredCapabilities,
                        node.Budget,
                        graphId,
                        node.PreferredRuntimeId,
                        node.OwnedPaths,
                        node.ExpectedFiles,
                        node.AllowNoOpCompletion)
                    : new TaskRequest(
                        taskId,
                        revisedDesc,
                        node?.RequiredCapabilities ?? new HashSet<string>(),
                        graphId,
                        node?.PreferredRuntimeId,
                        node?.OwnedPaths,
                        node?.ExpectedFiles,
                        node?.AllowNoOpCompletion ?? false);
                _dispatch.Tell(request);
                PersistGraphState(state);
            }
            else
            {
                _logger.LogWarning("Graph {GraphId}: task {TaskId} failed validation after {Max} attempts, marking failed",
                    graphId, taskId, maxAttempts);
                StorePlanningFeedback(
                    "planning-pitfall",
                    BuildValidationFailureFeedback(graphId, taskId, node, failures),
                    new Dictionary<string, string>
                    {
                        ["kind"] = "validation_failure",
                        ["graphId"] = graphId,
                        ["taskId"] = taskId
                    });
                if (state.AssignedAgent.TryGetValue(taskId, out var assignedAgentId))
                {
                    _viewport.Tell(new RuntimeOutput(
                        assignedAgentId,
                        $"[validator] Task {taskId} failed validation permanently after {maxAttempts} attempt(s): {failureSummary}"));
                }

                state.Status[taskId] = TaskNodeStatus.Failed;
                _viewport.Tell(new NotifyTaskNodeStatusChanged(graphId, taskId, TaskNodeStatus.Failed));
                CancelDependents(state, taskId);
                RequestWorkspaceRelease(state, taskId);
                CheckGraphCompletion(graphId, state);
                PersistGraphStateIfActive(graphId, state);
            }
        }
    }

    /// <summary>Find graph by task ID without a GraphId hint (for merge callbacks).</summary>
    private bool TryFindGraphByTask(string taskId, out string graphId, out GraphState state)
    {
        foreach (var (gid, gs) in _graphs)
        {
            if (gs.Nodes.ContainsKey(taskId))
            {
                graphId = gid;
                state = gs;
                return true;
            }
        }
        graphId = "";
        state = null!;
        return false;
    }

    private void HandleTaskFailed(TaskFailed failed)
    {
        if (TryHandlePlannerTaskFailed(failed))
            return;

        if (!TryFindGraph(failed.TaskId, failed.GraphId, out var graphId, out var state))
            return;

        state.Status[failed.TaskId] = TaskNodeStatus.Failed;
        _viewport.Tell(new NotifyTaskNodeStatusChanged(graphId, failed.TaskId, TaskNodeStatus.Failed));
        _logger.LogWarning("Graph {GraphId}: task {TaskId} failed — {Reason}",
            graphId, failed.TaskId, failed.Reason);
        StorePlanningFeedback(
            "planning-pitfall",
            BuildTaskFailureFeedback(graphId, state, failed),
            new Dictionary<string, string>
            {
                ["kind"] = "task_failure",
                ["graphId"] = graphId,
                ["taskId"] = failed.TaskId
            });

        CancelDependents(state, failed.TaskId);
        RequestWorkspaceRelease(state, failed.TaskId);
        CheckSubtasksCompleted(graphId, state, failed.TaskId);
        CheckGraphCompletion(graphId, state);
        PersistGraphStateIfActive(graphId, state);
    }

    private void HandleWorkspaceReleased(WorkspaceReleased released)
    {
        if (!TryFindGraphByTask(released.TaskId, out var graphId, out var state))
            return;

        if (!state.PendingWorkspaceRelease.Remove(released.TaskId))
            return;

        _logger.LogDebug("Graph {GraphId}: workspace released for task {TaskId}", graphId, released.TaskId);
        CheckGraphCompletion(graphId, state);
        PersistGraphStateIfActive(graphId, state);
    }

    private void RequestWorkspaceRelease(GraphState state, string taskId)
    {
        if (!state.PendingWorkspaceRelease.Add(taskId))
            return;

        _workspace.Tell(new ReleaseWorkspace(taskId));
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

            if (node.PlannerRequiredCapabilities is { Count: > 0 } && !state.PlannerResolved.Contains(taskId))
            {
                state.Status[taskId] = TaskNodeStatus.Planning;
                _viewport.Tell(new NotifyTaskNodeStatusChanged(state.GraphId, taskId, TaskNodeStatus.Planning));
                _logger.LogDebug("Graph {GraphId}: dispatching planner task for {TaskId}", state.GraphId, taskId);

                var plannerRequest = new TaskRequest(
                    BuildPlannerTaskId(taskId),
                    PromptBuilder.BuildDecompositionPrompt(taskId, node.Description, node.RequiredCapabilities, node.OwnedPaths, node.ExpectedFiles),
                    node.PlannerRequiredCapabilities,
                    state.GraphId,
                    node.PreferredPlannerRuntimeId,
                    node.OwnedPaths,
                    node.ExpectedFiles,
                    AllowNoOpCompletion: true);

                _dispatch.Tell(plannerRequest);
                continue;
            }

            // All deps satisfied — mark ready and dispatch
            state.Status[taskId] = TaskNodeStatus.Dispatched;
            _viewport.Tell(new NotifyTaskNodeStatusChanged(state.GraphId, taskId, TaskNodeStatus.Dispatched));
            _logger.LogDebug("Graph {GraphId}: dispatching task {TaskId}", state.GraphId, taskId);

            var request = node.Budget is not null
                ? new TaskRequestWithBudget(
                    taskId,
                    node.Description,
                    node.RequiredCapabilities,
                    node.Budget,
                    state.GraphId,
                    node.PreferredRuntimeId,
                    node.OwnedPaths,
                    node.ExpectedFiles,
                    node.AllowNoOpCompletion)
                : new TaskRequest(
                    taskId,
                    node.Description,
                    node.RequiredCapabilities,
                    state.GraphId,
                    node.PreferredRuntimeId,
                    node.OwnedPaths,
                    node.ExpectedFiles,
                    node.AllowNoOpCompletion);

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
                if (state.Status[dep] is TaskNodeStatus.Pending or TaskNodeStatus.Ready or TaskNodeStatus.Planning)
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

        // Cancel all non-terminal nodes including subtasks (snapshot keys to avoid mutation during iteration)
        foreach (var taskId in state.Status.Keys.ToList())
        {
            var status = state.Status[taskId];
            if (status is TaskNodeStatus.Pending or TaskNodeStatus.Ready or TaskNodeStatus.Planning or TaskNodeStatus.Dispatched
                or TaskNodeStatus.WaitingForSubtasks or TaskNodeStatus.Synthesizing or TaskNodeStatus.Validating)
            {
                state.Status[taskId] = status is TaskNodeStatus.Planning or TaskNodeStatus.Dispatched
                    or TaskNodeStatus.WaitingForSubtasks or TaskNodeStatus.Synthesizing or TaskNodeStatus.Validating
                    ? TaskNodeStatus.Failed
                    : TaskNodeStatus.Cancelled;
            }
        }

        CheckGraphCompletion(timedOut.GraphId, state);
        PersistGraphStateIfActive(timedOut.GraphId, state);
    }

    private void CheckGraphCompletion(string graphId, GraphState state)
    {
        var allTerminal = state.Status.Values.All(s =>
            s is TaskNodeStatus.Completed or TaskNodeStatus.Failed or TaskNodeStatus.Cancelled);

        if (!allTerminal || state.PendingWorkspaceRelease.Count > 0) return;

        var results = state.Status.ToDictionary(
            kv => kv.Key,
            kv => kv.Value == TaskNodeStatus.Completed);

        Timers.Cancel($"deadline-{graphId}");
        _graphs.Remove(graphId);
        _checkpointStore.Delete(graphId);
        var completed = new TaskGraphCompleted(graphId, results);
        _viewport.Tell(completed);
        Context.System.EventStream.Publish(completed);
        _logger.LogInformation("Graph {GraphId} completed: {Done}/{Total} succeeded",
            graphId,
            results.Values.Count(v => v),
            results.Count);
    }

    private void PersistGraphStateIfActive(string graphId, GraphState state)
    {
        if (_graphs.ContainsKey(graphId))
            PersistGraphState(state);
    }

    private void PersistGraphState(GraphState state)
    {
        try
        {
            _checkpointStore.Save(state.ToCheckpoint());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist checkpoint for graph {GraphId}", state.GraphId);
        }
    }

    private void RestoreCheckpoints()
    {
        foreach (var checkpoint in _checkpointStore.LoadAll())
        {
            try
            {
                var state = GraphState.FromCheckpoint(checkpoint, _logger);
                _graphs[state.GraphId] = state;
                _viewport.Tell(new NotifyTaskGraphSubmitted(state.GraphId, state.Nodes.Values.ToArray(), state.Edges));
                foreach (var (taskId, status) in state.Status)
                    _viewport.Tell(new NotifyTaskNodeStatusChanged(state.GraphId, taskId, status));

                if (state.GraphBudget?.Deadline is { } deadline)
                {
                    Timers.StartSingleTimer(
                        $"deadline-{state.GraphId}",
                        new GraphTimedOut(state.GraphId),
                        deadline);
                }

                DispatchReadyNodes(state);
                PersistGraphState(state);
                _logger.LogInformation("Restored task graph checkpoint {GraphId} with {NodeCount} nodes", state.GraphId, state.Nodes.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to restore task graph checkpoint {GraphId}", checkpoint.GraphId);
            }
        }
    }

    private void StorePlanningFeedback(string category, string content, IDictionary<string, string> tags)
    {
        if (_knowledgeSupervisor == ActorRefs.Nobody || string.IsNullOrWhiteSpace(content))
            return;

        _knowledgeSupervisor.Tell(new StoreKnowledge(PlannerKnowledgeAgentId, content, category, tags));
    }

    private static string BuildValidationFailureFeedback(
        string graphId,
        string taskId,
        TaskNode? node,
        IReadOnlyList<ValidatorResult> failures)
    {
        var sb = new StringBuilder();
        sb.Append("Planning pitfall for graph ").Append(graphId)
            .Append(", task ").Append(taskId)
            .Append(": validation failed after retries. ");
        if (node?.OwnedPaths is { Count: > 0 })
            sb.Append("owned_paths=[").Append(string.Join(", ", node.OwnedPaths)).Append("] ");
        if (node?.ExpectedFiles is { Count: > 0 })
            sb.Append("expected_files=[").Append(string.Join(", ", node.ExpectedFiles)).Append("] ");
        sb.Append("Failures: ")
            .Append(string.Join("; ", failures.Select(f => $"{f.ValidatorName}: {f.Details}")))
            .Append(". Consider narrowing the task scope or sequencing related file clusters.");
        return sb.ToString();
    }

    private static string BuildMergeConflictFeedback(string graphId, GraphState state, MergeConflict conflict)
    {
        var node = state.Nodes.GetValueOrDefault(conflict.TaskId);
        var sb = new StringBuilder();
        sb.Append("Planning pitfall for graph ").Append(graphId)
            .Append(", task ").Append(conflict.TaskId)
            .Append(": merge conflict occurred.");
        if (node?.OwnedPaths is { Count: > 0 })
            sb.Append(" owned_paths=[").Append(string.Join(", ", node.OwnedPaths)).Append("].");
        if (conflict.ConflictingFiles.Count > 0)
            sb.Append(" conflicting_files=[").Append(string.Join(", ", conflict.ConflictingFiles)).Append("].");
        sb.Append(" Do not split these file clusters in parallel.");
        return sb.ToString();
    }

    private static string BuildTaskFailureFeedback(string graphId, GraphState state, TaskFailed failed)
    {
        var node = state.Nodes.GetValueOrDefault(failed.TaskId);
        var sb = new StringBuilder();
        sb.Append("Planning pitfall for graph ").Append(graphId)
            .Append(", task ").Append(failed.TaskId)
            .Append(": task failed.");
        if (!string.IsNullOrWhiteSpace(failed.Reason))
            sb.Append(" reason=").Append(failed.Reason).Append('.');
        if (node?.OwnedPaths is { Count: > 0 })
            sb.Append(" owned_paths=[").Append(string.Join(", ", node.OwnedPaths)).Append("].");
        if (node?.ExpectedFiles is { Count: > 0 })
            sb.Append(" expected_files=[").Append(string.Join(", ", node.ExpectedFiles)).Append("].");
        sb.Append(" Re-plan this task instead of reusing the same split unchanged.");
        return sb.ToString();
    }

    private bool TryHandlePlannerTaskCompleted(TaskCompleted completed)
    {
        if (!TryResolvePlannerTask(completed.TaskId, completed.GraphId, out var graphId, out var state, out var parentTaskId))
            return false;

        state.PlannerResolved.Add(parentTaskId);
        state.AssignedAgent[parentTaskId] = completed.AgentId;

        if (completed.Subplan is not null)
        {
            var parentCompleted = completed with { TaskId = parentTaskId };
            HandleDecomposition(graphId, state, parentCompleted);
        }
        else
        {
            state.Status[parentTaskId] = TaskNodeStatus.Pending;
            _viewport.Tell(new NotifyTaskNodeStatusChanged(graphId, parentTaskId, TaskNodeStatus.Pending, completed.AgentId));
            _logger.LogInformation("Graph {GraphId}: planner chose direct execution for task {TaskId}",
                graphId, parentTaskId);
            DispatchReadyNodes(state);
            PersistGraphState(state);
        }

        _workspace.Tell(new ReleaseWorkspace(completed.TaskId));
        return true;
    }

    private bool TryHandlePlannerTaskFailed(TaskFailed failed)
    {
        if (!TryResolvePlannerTask(failed.TaskId, failed.GraphId, out var graphId, out var state, out var parentTaskId))
            return false;

        state.PlannerResolved.Add(parentTaskId);
        state.Status[parentTaskId] = TaskNodeStatus.Pending;
        _viewport.Tell(new NotifyTaskNodeStatusChanged(graphId, parentTaskId, TaskNodeStatus.Pending));
        _logger.LogWarning("Graph {GraphId}: planner failed for task {TaskId}, falling back to direct execution: {Reason}",
            graphId, parentTaskId, failed.Reason);
        StorePlanningFeedback(
            "planning-pitfall",
            $"Planning pitfall for graph {graphId}, task {parentTaskId}: planner task failed. reason={failed.Reason}. Falling back to direct execution.",
            new Dictionary<string, string>
            {
                ["kind"] = "planner_failure",
                ["graphId"] = graphId,
                ["taskId"] = parentTaskId
            });

        DispatchReadyNodes(state);
        PersistGraphState(state);
        _workspace.Tell(new ReleaseWorkspace(failed.TaskId));
        return true;
    }

    private bool TryResolvePlannerTask(string taskId, string? graphId, out string foundGraphId, out GraphState state, out string parentTaskId)
    {
        parentTaskId = string.Empty;
        if (!taskId.EndsWith(PlannerTaskSuffix, StringComparison.Ordinal))
        {
            foundGraphId = string.Empty;
            state = null!;
            return false;
        }

        parentTaskId = taskId[..^PlannerTaskSuffix.Length];
        if (TryFindGraph(parentTaskId, graphId, out foundGraphId, out state))
            return true;

        foundGraphId = string.Empty;
        state = null!;
        return false;
    }

    private static string BuildPlannerTaskId(string taskId) => $"{taskId}{PlannerTaskSuffix}";

    // ── Constants ──

    internal const int MaxDepth = 3;
    internal const int MaxSubtasksPerDecomposition = 10;
    internal const int MaxTotalNodes = 100;

    /// <summary>Internal state for a single task graph.</summary>
    internal sealed class GraphState
    {
        public required string GraphId { get; init; }
        public required TaskBudget? GraphBudget { get; init; }
        public required Dictionary<string, TaskNode> Nodes { get; init; }
        public required IReadOnlyList<TaskEdge> Edges { get; init; }
        public required Dictionary<string, List<string>> IncomingEdges { get; init; }
        public required Dictionary<string, List<string>> OutgoingEdges { get; init; }
        public required Dictionary<string, TaskNodeStatus> Status { get; init; }
        public required Dictionary<string, int> Depth { get; init; }
        public required Dictionary<string, string?> ParentTaskId { get; init; }
        public Dictionary<string, List<string>> ChildTaskIds { get; } = new();
        public Dictionary<string, StopCondition?> StopConditions { get; } = new();
        public Dictionary<string, string> AssignedAgent { get; } = new();
        public Dictionary<string, TaskCompleted> CompletedResults { get; } = new();
        public Dictionary<string, TaskCompleted> PendingMerge { get; } = new();
        public Dictionary<string, PendingValidationEntry> PendingValidation { get; } = new();
        public Dictionary<string, int> ValidationAttempts { get; } = new();
        public HashSet<string> PendingWorkspaceRelease { get; } = new();
        public HashSet<string> PlannerResolved { get; } = new();

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

            AddImplicitOwnershipEdges(nodes, incoming, outgoing, submit.Nodes.Select(node => node.TaskId).ToArray(), logger);

            graph = new CustomGraph();
            nodeMap = new Dictionary<string, Node>();
            foreach (var node in submit.Nodes)
                nodeMap[node.TaskId] = graph.AddNode();

            foreach (var (fromTaskId, toTaskIds) in outgoing)
            {
                foreach (var toTaskId in toTaskIds)
                    graph.AddArc(nodeMap[fromTaskId], nodeMap[toTaskId], Directedness.Directed);
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
                GraphBudget = submit.GraphBudget,
                Nodes = nodes,
                Edges = submit.Edges.ToArray(),
                IncomingEdges = incoming,
                OutgoingEdges = outgoing,
                Status = status,
                Depth = depth,
                ParentTaskId = parentTaskId,
            }, null);
        }

        public TaskGraphCheckpoint ToCheckpoint()
        {
            return new TaskGraphCheckpoint(
                GraphId,
                GraphBudget,
                Nodes.Values.OrderBy(node => node.TaskId).ToArray(),
                Edges.ToArray(),
                new Dictionary<string, TaskNodeStatus>(Status),
                new Dictionary<string, int>(Depth),
                new Dictionary<string, string?>(ParentTaskId),
                ChildTaskIds.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.ToArray()),
                StopConditions.ToDictionary(kv => kv.Key, kv => kv.Value),
                new Dictionary<string, string>(AssignedAgent),
                new Dictionary<string, TaskCompleted>(CompletedResults),
                new Dictionary<string, TaskCompleted>(PendingMerge),
                PendingValidation.ToDictionary(
                    kv => kv.Key,
                    kv => new PendingValidationCheckpoint(
                        kv.Value.Completed,
                        kv.Value.RemainingArtifacts,
                        kv.Value.AllResults.ToArray())),
                new Dictionary<string, int>(ValidationAttempts),
                PendingWorkspaceRelease.OrderBy(id => id).ToArray(),
                PlannerResolved.OrderBy(id => id).ToArray());
        }

        public static GraphState FromCheckpoint(TaskGraphCheckpoint checkpoint, ILogger logger)
        {
            var submit = new SubmitTaskGraph(
                checkpoint.GraphId,
                checkpoint.Nodes,
                checkpoint.Edges,
                checkpoint.GraphBudget);

            var (state, rejectReason) = TryCreate(submit, logger);
            if (state is null)
                throw new InvalidOperationException($"Failed to restore checkpoint {checkpoint.GraphId}: {rejectReason}");

            foreach (var (taskId, status) in checkpoint.Status)
                state.Status[taskId] = NormalizeRestoredStatus(status);

            foreach (var (taskId, depth) in checkpoint.Depth)
                state.Depth[taskId] = depth;

            foreach (var (taskId, parentTaskId) in checkpoint.ParentTaskId)
                state.ParentTaskId[taskId] = parentTaskId;

            foreach (var (taskId, childIds) in checkpoint.ChildTaskIds)
                state.ChildTaskIds[taskId] = childIds.ToList();

            foreach (var (taskId, stopCondition) in checkpoint.StopConditions)
                state.StopConditions[taskId] = stopCondition;

            foreach (var (taskId, agentId) in checkpoint.AssignedAgent)
                state.AssignedAgent[taskId] = agentId;

            foreach (var (taskId, result) in checkpoint.CompletedResults)
                state.CompletedResults[taskId] = result;

            foreach (var (taskId, attempts) in checkpoint.ValidationAttempts)
                state.ValidationAttempts[taskId] = attempts;

            foreach (var taskId in checkpoint.PlannerResolved)
                state.PlannerResolved.Add(taskId);

            return state;
        }

        private static TaskNodeStatus NormalizeRestoredStatus(TaskNodeStatus status)
        {
            return status switch
            {
                TaskNodeStatus.Pending => TaskNodeStatus.Pending,
                TaskNodeStatus.Planning => TaskNodeStatus.Pending,
                TaskNodeStatus.Completed => TaskNodeStatus.Completed,
                TaskNodeStatus.Failed => TaskNodeStatus.Failed,
                TaskNodeStatus.Cancelled => TaskNodeStatus.Cancelled,
                TaskNodeStatus.WaitingForSubtasks => TaskNodeStatus.WaitingForSubtasks,
                TaskNodeStatus.Synthesizing => TaskNodeStatus.Synthesizing,
                _ => TaskNodeStatus.Pending
            };
        }

        internal static void AddImplicitOwnershipEdges(
            IReadOnlyDictionary<string, TaskNode> nodes,
            Dictionary<string, List<string>> incoming,
            Dictionary<string, List<string>> outgoing,
            IReadOnlyList<string> orderedTaskIds,
            ILogger logger)
        {
            for (var i = 0; i < orderedTaskIds.Count; i++)
            {
                var firstId = orderedTaskIds[i];
                if (!nodes.TryGetValue(firstId, out var firstNode) ||
                    firstNode.OwnedPaths is not { Count: > 0 })
                {
                    continue;
                }

                for (var j = i + 1; j < orderedTaskIds.Count; j++)
                {
                    var secondId = orderedTaskIds[j];
                    if (!nodes.TryGetValue(secondId, out var secondNode) ||
                        secondNode.OwnedPaths is not { Count: > 0 } ||
                        !OwnedPathsOverlap(firstNode.OwnedPaths, secondNode.OwnedPaths))
                    {
                        continue;
                    }

                    if (HasPath(outgoing, firstId, secondId) || HasPath(outgoing, secondId, firstId))
                        continue;

                    outgoing[firstId].Add(secondId);
                    incoming[secondId].Add(firstId);
                    logger.LogInformation(
                        "Implicitly sequencing task {FirstTaskId} before {SecondTaskId} because owned_paths overlap.",
                        firstId,
                        secondId);
                }
            }
        }

        private static bool OwnedPathsOverlap(IReadOnlyList<string> leftPaths, IReadOnlyList<string> rightPaths)
        {
            foreach (var left in leftPaths)
            {
                var normalizedLeft = NormalizeOwnedPath(left);
                if (string.IsNullOrEmpty(normalizedLeft))
                    continue;

                foreach (var right in rightPaths)
                {
                    var normalizedRight = NormalizeOwnedPath(right);
                    if (string.IsNullOrEmpty(normalizedRight))
                        continue;

                    if (PathPrefixesOverlap(normalizedLeft, normalizedRight))
                        return true;
                }
            }

            return false;
        }

        private static bool HasPath(Dictionary<string, List<string>> outgoing, string fromTaskId, string toTaskId)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>();
            stack.Push(fromTaskId);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (!visited.Add(current))
                    continue;

                if (!outgoing.TryGetValue(current, out var next))
                    continue;

                foreach (var candidate in next)
                {
                    if (string.Equals(candidate, toTaskId, StringComparison.Ordinal))
                        return true;

                    stack.Push(candidate);
                }
            }

            return false;
        }

        private static string NormalizeOwnedPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            var normalized = path.Trim().Replace('\\', '/');
            while (normalized.StartsWith("./", StringComparison.Ordinal))
                normalized = normalized[2..];

            normalized = normalized.Trim('/');
            return normalized;
        }

        private static bool PathPrefixesOverlap(string left, string right)
        {
            return IsPathPrefix(left, right) || IsPathPrefix(right, left);
        }

        private static bool IsPathPrefix(string prefix, string candidate)
        {
            if (string.Equals(prefix, candidate, StringComparison.OrdinalIgnoreCase))
                return true;

            return candidate.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
        }
    }

    private record GraphTimedOut(string GraphId);

    internal sealed class PendingValidationEntry
    {
        public TaskCompleted Completed { get; }
        public int RemainingArtifacts { get; set; }
        public List<ValidatorResult> AllResults { get; }

        public PendingValidationEntry(TaskCompleted completed, int remainingArtifacts, List<ValidatorResult> allResults)
        {
            Completed = completed;
            RemainingArtifacts = remainingArtifacts;
            AllResults = allResults;
        }
    }
}
