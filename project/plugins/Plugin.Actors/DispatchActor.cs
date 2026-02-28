using Akka.Actor;
using GiantIsopod.Contracts.Core;
using Microsoft.Extensions.Logging;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// /user/dispatch — routes tasks to agents based on capability requirements.
/// Queries the skill registry, selects best-fit agent, forwards task.
/// </summary>
public sealed class DispatchActor : UntypedActor
{
    private readonly IActorRef _registry;
    private readonly IActorRef _agentSupervisor;
    private readonly ILogger<DispatchActor> _logger;

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
        }
    }

    private void HandleRegistryResponse(object message, TaskRequest pendingRequest)
    {
        if (message is CapableAgentsResult result)
        {
            UnbecomeStacked();

            if (result.AgentIds.Count > 0)
            {
                var selectedAgent = result.AgentIds[0];
                _logger.LogInformation("Task {TaskId} assigned to {AgentId}", pendingRequest.TaskId, selectedAgent);
                var assignment = new TaskAssigned(pendingRequest.TaskId, selectedAgent);
                _agentSupervisor.Tell(assignment);
                Sender.Tell(assignment);
            }
            else
            {
                _logger.LogWarning("No agent satisfies capability requirement for task {TaskId}", pendingRequest.TaskId);
                Sender.Tell(new TaskFailed(
                    pendingRequest.TaskId,
                    "No agent satisfies the capability requirement",
                    pendingRequest.RequiredCapabilities));
            }
        }
        else
        {
            OnReceive(message);
        }
    }
}
