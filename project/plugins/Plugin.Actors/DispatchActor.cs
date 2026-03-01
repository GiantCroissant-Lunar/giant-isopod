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
    private readonly ILogger<DispatchActor> _logger;

    /// <summary>Active bid sessions keyed by TaskId.</summary>
    private readonly Dictionary<string, BidSession> _bidSessions = new();

    /// <summary>Resolved assignments awaiting risk approval, keyed by TaskId.</summary>
    private readonly Dictionary<string, PendingApproval> _pendingApprovals = new();

    private static readonly TimeSpan DefaultBidWindow = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ApprovalTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Only accept RiskApproved/RiskDenied from the viewport actor.</summary>
    private const string TrustedApproverPath = "/user/viewport";

    public ITimerScheduler Timers { get; set; } = null!;

    public DispatchActor(IActorRef registry, IActorRef agentSupervisor, ILogger<DispatchActor> logger)
    {
        _registry = registry;
        _agentSupervisor = agentSupervisor;
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

            // Start bid session — use captured requester, not Sender (which is now the registry)
            var session = new BidSession(pendingRequest, result.AgentIds, requester);
            _bidSessions[pendingRequest.TaskId] = session;

            // Broadcast TaskAvailable to all capable agents
            var available = new TaskAvailable(
                pendingRequest.TaskId,
                pendingRequest.Description,
                pendingRequest.RequiredCapabilities,
                DefaultBidWindow);

            foreach (var agentId in result.AgentIds)
            {
                _agentSupervisor.Tell(new ForwardToAgent(agentId, available));
            }

            // Start bid window timer
            Timers.StartSingleTimer(
                $"bid-{pendingRequest.TaskId}",
                new BidWindowClosed(pendingRequest.TaskId),
                DefaultBidWindow);

            _logger.LogDebug("Bid window opened for task {TaskId} ({Count} agents)",
                pendingRequest.TaskId, result.AgentIds.Count);
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

        if (session.Bids.Count > 0)
        {
            // Select winner: highest fitness, then lowest load, then shortest duration
            var winner = session.Bids
                .OrderByDescending(b => b.Fitness)
                .ThenBy(b => b.ActiveTaskCount)
                .ThenBy(b => b.EstimatedDuration)
                .First();

            selectedAgentId = winner.AgentId;
            budget = (session.Request as TaskRequestWithBudget)?.Budget;

            _logger.LogInformation("Task {TaskId} awarded to {AgentId} via bid (fitness={Fitness:F2}, {BidCount} bids)",
                taskId, selectedAgentId, winner.Fitness, session.Bids.Count);

            // Reject losers immediately
            foreach (var loser in session.Bids.Where(b => b.AgentId != selectedAgentId))
            {
                _agentSupervisor.Tell(new ForwardToAgent(loser.AgentId, new TaskBidRejected(taskId, loser.AgentId)));
            }
        }
        else
        {
            // Fallback: first-match assignment (original behavior)
            _logger.LogWarning("No bids received for task {TaskId}, using first-match fallback", taskId);
            selectedAgentId = session.CapableAgents[0];
            budget = (session.Request as TaskRequestWithBudget)?.Budget;
        }

        // Risk approval gate: Critical tasks require viewport approval before assignment
        if (budget?.Risk == RiskLevel.Critical)
        {
            _pendingApprovals[taskId] = new PendingApproval(taskId, selectedAgentId, session.Request.Description, budget, session.OriginalSender, session.Request.GraphId);
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

        AwardTask(taskId, selectedAgentId, session.Request.Description, budget, session.OriginalSender, session.Request.GraphId);
    }

    private void AwardTask(string taskId, string agentId, string? description, TaskBudget? budget, IActorRef originalSender, string? graphId = null)
    {
        var assignment = new TaskAssigned(taskId, agentId, description, budget, graphId);
        _agentSupervisor.Tell(assignment);
        originalSender.Tell(assignment);
    }

    private bool IsFromTrustedApprover()
    {
        return Sender.Path.ToString().EndsWith(TrustedApproverPath);
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
        AwardTask(taskId, pending.AgentId, pending.Description, pending.Budget, pending.OriginalSender, pending.GraphId);
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
    private sealed record ApprovalTimedOut(string TaskId);
    private sealed record PendingApproval(string TaskId, string AgentId, string? Description, TaskBudget? Budget, IActorRef OriginalSender, string? GraphId = null);
}

/// <summary>
/// Message to route a payload to a specific agent via the AgentSupervisor.
/// </summary>
public record ForwardToAgent(string AgentId, object Payload);
