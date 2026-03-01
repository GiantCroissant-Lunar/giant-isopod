using Akka.Actor;
using GiantIsopod.Contracts.Core;
using Microsoft.Extensions.Logging;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// /user/dispatch — routes tasks to agents via market-first bidding with
/// orchestrator fallback. Queries the skill registry for capable agents,
/// broadcasts TaskAvailable, collects bids, selects winner.
/// Falls back to first-match if no bids arrive within the bid window.
/// </summary>
public sealed class DispatchActor : UntypedActor, IWithTimers
{
    private readonly IActorRef _registry;
    private readonly IActorRef _agentSupervisor;
    private readonly ILogger<DispatchActor> _logger;

    /// <summary>Active bid sessions keyed by TaskId.</summary>
    private readonly Dictionary<string, BidSession> _bidSessions = new();

    private static readonly TimeSpan DefaultBidWindow = TimeSpan.FromMilliseconds(500);

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
                _registry.Tell(new QueryCapableAgents(request.RequiredCapabilities));
                BecomeStacked(msg => HandleRegistryResponse(msg, request));
                break;

            case TaskBid bid:
                HandleBid(bid);
                break;

            case BidWindowClosed closed:
                ResolveBidSession(closed.TaskId);
                break;
        }
    }

    private void HandleRegistryResponse(object message, TaskRequest pendingRequest)
    {
        if (message is CapableAgentsResult result)
        {
            UnbecomeStacked();

            if (result.AgentIds.Count == 0)
            {
                _logger.LogWarning("No agent satisfies capability requirement for task {TaskId}", pendingRequest.TaskId);
                Sender.Tell(new TaskFailed(
                    pendingRequest.TaskId,
                    "No agent satisfies the capability requirement",
                    pendingRequest.RequiredCapabilities));
                return;
            }

            // Start bid session
            var session = new BidSession(pendingRequest, result.AgentIds, Sender);
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
            // Bid arrived after session closed — ignore
            _logger.LogDebug("Late bid from {AgentId} for task {TaskId}, ignoring", bid.AgentId, bid.TaskId);
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

        if (session.Bids.Count > 0)
        {
            // Select winner: highest fitness, then lowest load, then shortest duration
            var winner = session.Bids
                .OrderByDescending(b => b.Fitness)
                .ThenBy(b => b.ActiveTaskCount)
                .ThenBy(b => b.EstimatedDuration)
                .First();

            _logger.LogInformation("Task {TaskId} awarded to {AgentId} via bid (fitness={Fitness:F2}, {BidCount} bids)",
                taskId, winner.AgentId, winner.Fitness, session.Bids.Count);

            // Notify winner and losers — preserve budget from original request
            var budget = (session.Request as TaskRequestWithBudget)?.Budget;
            var assignment = new TaskAssigned(taskId, winner.AgentId, budget);
            _agentSupervisor.Tell(assignment);
            session.OriginalSender.Tell(assignment);

            foreach (var loser in session.Bids.Where(b => b.AgentId != winner.AgentId))
            {
                _agentSupervisor.Tell(new ForwardToAgent(loser.AgentId, new TaskBidRejected(taskId, loser.AgentId)));
            }
        }
        else
        {
            // Fallback: first-match assignment (original behavior)
            _logger.LogWarning("No bids received for task {TaskId}, using first-match fallback", taskId);
            var selectedAgent = session.CapableAgents[0];
            var fallbackBudget = (session.Request as TaskRequestWithBudget)?.Budget;
            var fallbackAssignment = new TaskAssigned(taskId, selectedAgent, fallbackBudget);
            _agentSupervisor.Tell(fallbackAssignment);
            session.OriginalSender.Tell(fallbackAssignment);
        }
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
}

/// <summary>
/// Message to route a payload to a specific agent via the AgentSupervisor.
/// </summary>
public record ForwardToAgent(string AgentId, object Payload);
