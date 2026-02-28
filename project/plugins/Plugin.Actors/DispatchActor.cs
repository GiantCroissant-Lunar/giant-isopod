using Akka.Actor;
using GiantIsopod.Contracts.Core;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// /user/dispatch — routes tasks to agents based on capability requirements.
/// Queries the skill registry, selects best-fit agent, forwards task.
/// </summary>
public sealed class DispatchActor : UntypedActor
{
    private readonly IActorRef _registry;
    private readonly IActorRef _agentSupervisor;

    public DispatchActor(IActorRef registry, IActorRef agentSupervisor)
    {
        _registry = registry;
        _agentSupervisor = agentSupervisor;
    }

    protected override void OnReceive(object message)
    {
        switch (message)
        {
            case TaskRequest request:
                // Query registry for capable agents
                _registry.Tell(new QueryCapableAgents(request.RequiredCapabilities));
                // Store pending request to match with response
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
                // Select first capable agent (TODO: ranking by load, cost, etc.)
                var selectedAgent = result.AgentIds[0];
                var assignment = new TaskAssigned(pendingRequest.TaskId, selectedAgent);
                _agentSupervisor.Tell(assignment);
                Sender.Tell(assignment);
            }
            else
            {
                Sender.Tell(new TaskFailed(
                    pendingRequest.TaskId,
                    "No agent satisfies the capability requirement",
                    pendingRequest.RequiredCapabilities));
            }
        }
        else
        {
            // Forward other messages to default handler
            OnReceive(message);
        }
    }
}
