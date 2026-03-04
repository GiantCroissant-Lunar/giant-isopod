using Akka.Actor;
using GiantIsopod.Contracts.Core;
using Microsoft.Extensions.Logging;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// /user/dispatch — routes tasks to agents via market-first bidding with
/// orchestrator fallback. Queries the skill registry for capable agents,
/// broadcasts TaskAvailable, collects bids, selects winner.
/// Falls back to first-match if no bids arrive within the bid window.
/// Critical-risk tasks require viewport approval before assignment.
/// </summary>
public sealed class DispatchActor : UntypedActor, IWithTimers
{
    private readonly IActorRef _registry;
    private readonly IActorRef _agentSupervisor;
    private readonly IActorRef _workspace;
    private readonly ILogger<DispatchActor> _logger;

    /// <summary>Active bid sessions keyed by TaskId.</summary>
    private readonly Dictionary<string, BidSession> _bidSessions = new();

    /// <summary>Resolved assignments awaiting risk approval, keyed by TaskId.</summary>
    private readonly Dictionary<string, PendingApproval> _pendingApprovals = new();
    private readonly Dictionary<string, int> _pendingAgentReservations = new(StringComparer.Ordinal);

    private static readonly TimeSpan DefaultBidWindow = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ApprovalTimeout = TimeSpan.FromSeconds(30);
    private const int MaxConcurrentTasksPerAgent = 1;

    /// <summary>Only accept RiskApproved/RiskDenied from the viewport actor.</summary>
    private const string TrustedApproverPath = "/user/viewport";

    public ITimerScheduler Timers { get; set; } = null!;

    private static readonly TimeSpan AllocationTimeout = TimeSpan.FromSeconds(10);

    public DispatchActor(IActorRef registry, IActorRef agentSupervisor, IActorRef workspace, ILogger<DispatchActor> logger)
    {
        _registry = registry;
        _agentSupervisor = agentSupervisor;
        _workspace = workspace;
        _logger = logger;
    }

    protected override void OnReceive(object message)
    {
        switch (message)
        {
            case TaskRequest request:
                _logger.LogDebug("Dispatching task {TaskId}", request.TaskId);
                var requester = Sender; // capture before BecomeStacked changes Sender
                _registry.Tell(new QueryCapableAgents(request.RequiredCapabilities));
                BecomeStacked(msg => HandleRegistryResponse(msg, request, requester));
                break;

            case TaskBid bid:
                HandleBid(bid);
                break;

            case BidWindowClosed closed:
                ResolveBidSession(closed.TaskId);
                break;

            case RiskApproved approved:
                if (!IsFromTrustedApprover())
                {
                    _logger.LogWarning("Rejected RiskApproved for {TaskId} from untrusted sender {Sender}",
                        approved.TaskId, Sender.Path);
                    break;
                }
                HandleRiskApproved(approved.TaskId);
                break;

            case RiskDenied denied:
                if (!IsFromTrustedApprover())
                {
                    _logger.LogWarning("Rejected RiskDenied for {TaskId} from untrusted sender {Sender}",
                        denied.TaskId, Sender.Path);
                    break;
                }
                HandleRiskDenied(denied.TaskId, denied.Reason);
                break;

            case ApprovalTimedOut timedOut:
                HandleApprovalTimeout(timedOut.TaskId);
                break;

            case WorkspaceAllocationDone done:
                HandleWorkspaceAllocationDone(done);
                break;

            case RetryBidSession retry:
                OpenBidSession(new BidSession(retry.Request, retry.CapableAgents, retry.OriginalSender));
                break;

            case AgentCapacityAvailable available:
                ReleaseAgentReservation(available.AgentId);
                break;
        }
    }

    private void HandleRegistryResponse(object message, TaskRequest pendingRequest, IActorRef requester)
    {
        if (message is CapableAgentsResult result)
        {
            UnbecomeStacked();

            if (result.AgentIds.Count == 0)
            {
                _logger.LogWarning("No agent satisfies capability requirement for task {TaskId}", pendingRequest.TaskId);
                requester.Tell(new TaskFailed(
                    pendingRequest.TaskId,
                    "No agent satisfies the capability requirement",
                    pendingRequest.RequiredCapabilities));
                return;
            }

            OpenBidSession(new BidSession(pendingRequest, result.AgentIds, requester));
        }
        else
        {
            // Forward other messages to main handler (e.g. bids arriving during registry query)
            OnReceive(message);
        }
    }

    private void HandleBid(TaskBid bid)
    {
        if (!_bidSessions.TryGetValue(bid.TaskId, out var session))
        {
            _logger.LogDebug("Late bid from {AgentId} for task {TaskId}, ignoring", bid.AgentId, bid.TaskId);
            return;
        }

        // Reject bids from agents not in the capable list
        if (!session.CapableAgents.Contains(bid.AgentId))
        {
            _logger.LogDebug("Bid from non-capable agent {AgentId} for task {TaskId}, ignoring", bid.AgentId, bid.TaskId);
            return;
        }

        // Reject duplicate bids from the same agent
        if (session.Bids.Any(b => b.AgentId == bid.AgentId))
        {
            _logger.LogDebug("Duplicate bid from {AgentId} for task {TaskId}, ignoring", bid.AgentId, bid.TaskId);
            return;
        }

        session.Bids.Add(bid);
        _logger.LogDebug("Received bid from {AgentId} for task {TaskId} (fitness={Fitness:F2}, load={Load})",
            bid.AgentId, bid.TaskId, bid.Fitness, bid.ActiveTaskCount);
    }

    private void ResolveBidSession(string taskId)
    {
        if (!_bidSessions.Remove(taskId, out var session))
            return;

        string selectedAgentId;
        TaskBudget? budget;

        var eligibleBids = session.Bids
            .Where(b => EffectiveLoad(b) < MaxConcurrentTasksPerAgent)
            .ToList();

        if (eligibleBids.Count > 0)
        {
            var preferredRuntimeId = session.Request.PreferredRuntimeId;
            var preferredBids = string.IsNullOrWhiteSpace(preferredRuntimeId)
                ? eligibleBids
                : eligibleBids.Where(b => IsPreferredRuntimeMatch(b, preferredRuntimeId)).ToList();
            var candidateBids = preferredBids.Count > 0
                ? preferredBids
                : eligibleBids;

            // Select winner: preferred runtime first when available, then highest fitness, lowest load, shortest duration.
            var winner = candidateBids
                .OrderByDescending(b => b.Fitness)
                .ThenBy(b => EffectiveLoad(b))
                .ThenBy(b => b.EstimatedDuration)
                .First();

            selectedAgentId = winner.AgentId;
            budget = (session.Request as TaskRequestWithBudget)?.Budget;

            _logger.LogInformation(
                "Task {TaskId} awarded to {AgentId} via bid (fitness={Fitness:F2}, runtime={RuntimeId}, preferredRuntime={PreferredRuntimeId}, {BidCount} bids)",
                taskId, selectedAgentId, winner.Fitness, winner.RuntimeId ?? "<unknown>", preferredRuntimeId ?? "<none>", session.Bids.Count);

            // Reject losers immediately
            foreach (var loser in session.Bids.Where(b => b.AgentId != selectedAgentId))
            {
                _agentSupervisor.Tell(new ForwardToAgent(loser.AgentId, new TaskBidRejected(taskId, loser.AgentId)));
            }
        }
        else
        {
            if (session.CapableAgents.All(IsAgentAtCapacity))
            {
                RequeueBidSession(session);
                return;
            }

            // Fallback: first-match assignment (original behavior)
            _logger.LogWarning("No bids received for task {TaskId}, using first-match fallback", taskId);
            selectedAgentId = session.CapableAgents.First(agentId => !IsAgentAtCapacity(agentId));
            budget = (session.Request as TaskRequestWithBudget)?.Budget;
        }

        // Risk approval gate: Critical tasks require viewport approval before assignment
        if (budget?.Risk == RiskLevel.Critical)
        {
            _pendingApprovals[taskId] = new PendingApproval(
                taskId,
                selectedAgentId,
                session.Request.Description,
                budget,
                session.OriginalSender,
                session.Request.GraphId,
                session.Request.OwnedPaths,
                session.Request.ExpectedFiles,
                session.Request.RequiredCapabilities,
                session.Request.AllowNoOpCompletion);
            var approval = new RiskApprovalRequired(taskId, RiskLevel.Critical, session.Request.Description);
            Context.System.EventStream.Publish(approval);

            // Start timeout timer to prevent indefinite pending state
            Timers.StartSingleTimer(
                $"approval-{taskId}",
                new ApprovalTimedOut(taskId),
                ApprovalTimeout);

            _logger.LogWarning("Task {TaskId} requires risk approval (Critical) — assignment to {AgentId} held ({Timeout}s timeout)",
                taskId, selectedAgentId, ApprovalTimeout.TotalSeconds);
            return;
        }

        AwardTask(
            taskId,
            selectedAgentId,
            session.Request.Description,
            budget,
            session.OriginalSender,
            session.Request.GraphId,
            session.Request.OwnedPaths,
            session.Request.ExpectedFiles,
            session.Request.RequiredCapabilities,
            session.Request.AllowNoOpCompletion);
    }

    private void AwardTask(
        string taskId,
        string agentId,
        string? description,
        TaskBudget? budget,
        IActorRef originalSender,
        string? graphId = null,
        IReadOnlyList<string>? ownedPaths = null,
        IReadOnlyList<string>? expectedFiles = null,
        IReadOnlySet<string>? requiredCapabilities = null,
        bool allowNoOpCompletion = false)
    {
        // Attempt workspace allocation; on failure or timeout, award without workspace (graceful degradation)
        _workspace.Ask<object>(new AllocateWorkspace(taskId, "HEAD"), AllocationTimeout)
            .ContinueWith(t =>
            {
                string? workspacePath = null;
                if (t.IsCompletedSuccessfully && t.Result is WorkspaceAllocated allocated)
                    workspacePath = allocated.WorktreePath;

                return new WorkspaceAllocationDone(taskId, agentId, description, budget, originalSender, graphId, workspacePath, ownedPaths, expectedFiles, requiredCapabilities, allowNoOpCompletion);
            })
            .PipeTo(Self);

        ReserveAgent(agentId);
    }

    private void HandleWorkspaceAllocationDone(WorkspaceAllocationDone done)
    {
        if (done.WorkspacePath != null)
        {
            _logger.LogDebug("Task {TaskId} awarded with workspace at {Path}", done.TaskId, done.WorkspacePath);
        }
        else
        {
            _logger.LogDebug("Task {TaskId} awarded without workspace (allocation skipped or failed)", done.TaskId);
        }

        var assignment = new TaskAssigned(
            done.TaskId,
            done.AgentId,
            done.Description,
            done.Budget,
            done.GraphId,
            done.WorkspacePath,
            done.OwnedPaths,
            done.ExpectedFiles,
            done.RequiredCapabilities,
            done.AllowNoOpCompletion);
        _agentSupervisor.Tell(assignment);
        done.OriginalSender.Tell(assignment);
    }

    private bool IsFromTrustedApprover()
    {
        return Sender.Path.ToString().EndsWith(TrustedApproverPath);
    }

    private int EffectiveLoad(TaskBid bid)
    {
        return bid.ActiveTaskCount + _pendingAgentReservations.GetValueOrDefault(bid.AgentId, 0);
    }

    private bool IsAgentAtCapacity(string agentId)
    {
        return _pendingAgentReservations.GetValueOrDefault(agentId, 0) >= MaxConcurrentTasksPerAgent;
    }

    private void ReserveAgent(string agentId)
    {
        _pendingAgentReservations[agentId] = _pendingAgentReservations.GetValueOrDefault(agentId, 0) + 1;
    }

    private void ReleaseAgentReservation(string agentId)
    {
        if (!_pendingAgentReservations.TryGetValue(agentId, out var current))
            return;

        if (current <= 1)
        {
            _pendingAgentReservations.Remove(agentId);
            return;
        }

        _pendingAgentReservations[agentId] = current - 1;
    }

    private void RequeueBidSession(BidSession session)
    {
        _logger.LogDebug("Task {TaskId} deferred because all capable agents are currently at capacity", session.Request.TaskId);
        Timers.StartSingleTimer(
            $"retry-bid-{session.Request.TaskId}",
            new RetryBidSession(session.Request, session.CapableAgents, session.OriginalSender),
            DefaultBidWindow);
    }

    private void OpenBidSession(BidSession session)
    {
        _bidSessions[session.Request.TaskId] = session;

        var available = new TaskAvailable(
            session.Request.TaskId,
            session.Request.Description,
            session.Request.RequiredCapabilities,
            DefaultBidWindow,
            session.Request.PreferredRuntimeId);

        foreach (var agentId in session.CapableAgents)
        {
            _agentSupervisor.Tell(new ForwardToAgent(agentId, available));
        }

        Timers.StartSingleTimer(
            $"bid-{session.Request.TaskId}",
            new BidWindowClosed(session.Request.TaskId),
            DefaultBidWindow);

        _logger.LogDebug("Bid window opened for task {TaskId} ({Count} agents)",
            session.Request.TaskId, session.CapableAgents.Count);
    }

    private static bool IsPreferredRuntimeMatch(TaskBid bid, string preferredRuntimeId)
    {
        return !string.IsNullOrWhiteSpace(bid.RuntimeId)
               && string.Equals(bid.RuntimeId, preferredRuntimeId, StringComparison.OrdinalIgnoreCase);
    }

    private void HandleRiskApproved(string taskId)
    {
        if (!_pendingApprovals.Remove(taskId, out var pending))
        {
            _logger.LogDebug("Risk approval for unknown task {TaskId}, ignoring", taskId);
            return;
        }

        Timers.Cancel($"approval-{taskId}");
        _logger.LogInformation("Task {TaskId} risk approved — assigning to {AgentId}", taskId, pending.AgentId);
        AwardTask(
            taskId,
            pending.AgentId,
            pending.Description,
            pending.Budget,
            pending.OriginalSender,
            pending.GraphId,
            pending.OwnedPaths,
            pending.ExpectedFiles,
            pending.RequiredCapabilities,
            pending.AllowNoOpCompletion);
    }

    private void HandleRiskDenied(string taskId, string reason)
    {
        if (!_pendingApprovals.Remove(taskId, out var pending))
        {
            _logger.LogDebug("Risk denial for unknown task {TaskId}, ignoring", taskId);
            return;
        }

        Timers.Cancel($"approval-{taskId}");
        _logger.LogWarning("Task {TaskId} risk denied: {Reason}", taskId, reason);
        pending.OriginalSender.Tell(new TaskFailed(taskId, $"Risk denied: {reason}"));
    }

    private void HandleApprovalTimeout(string taskId)
    {
        if (!_pendingApprovals.Remove(taskId, out var pending))
            return;

        _logger.LogWarning("Task {TaskId} risk approval timed out after {Timeout}s — denying",
            taskId, ApprovalTimeout.TotalSeconds);
        pending.OriginalSender.Tell(new TaskFailed(taskId, "Risk approval timed out"));
    }

    private sealed class BidSession
    {
        public TaskRequest Request { get; }
        public IReadOnlyList<string> CapableAgents { get; }
        public IActorRef OriginalSender { get; }
        public List<TaskBid> Bids { get; } = new();

        public BidSession(TaskRequest request, IReadOnlyList<string> capableAgents, IActorRef originalSender)
        {
            Request = request;
            CapableAgents = capableAgents;
            OriginalSender = originalSender;
        }
    }

    private sealed record BidWindowClosed(string TaskId);
    private sealed record RetryBidSession(TaskRequest Request, IReadOnlyList<string> CapableAgents, IActorRef OriginalSender);
    private sealed record ApprovalTimedOut(string TaskId);
    private sealed record PendingApproval(
        string TaskId,
        string AgentId,
        string? Description,
        TaskBudget? Budget,
        IActorRef OriginalSender,
        string? GraphId = null,
        IReadOnlyList<string>? OwnedPaths = null,
        IReadOnlyList<string>? ExpectedFiles = null,
        IReadOnlySet<string>? RequiredCapabilities = null,
        bool AllowNoOpCompletion = false);
    private sealed record WorkspaceAllocationDone(
        string TaskId,
        string AgentId,
        string? Description,
        TaskBudget? Budget,
        IActorRef OriginalSender,
        string? GraphId,
        string? WorkspacePath,
        IReadOnlyList<string>? OwnedPaths,
        IReadOnlyList<string>? ExpectedFiles,
        IReadOnlySet<string>? RequiredCapabilities,
        bool AllowNoOpCompletion);
}

/// <summary>
/// Message to route a payload to a specific agent via the AgentSupervisor.
/// </summary>
public record ForwardToAgent(string AgentId, object Payload);
public record AgentCapacityAvailable(string AgentId);
