using Akka.Actor;
using GiantIsopod.Contracts.Core;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// /user/viewport — observer bridge to Godot. Receives agent state events
/// and pushes them to the ECS via a thread-safe queue. Never commands agents.
/// </summary>
public sealed class ViewportActor : UntypedActor
{
    private IViewportBridge? _bridge;

    protected override void OnReceive(object message)
    {
        switch (message)
        {
            case SetViewportBridge setBridge:
                _bridge = setBridge.Bridge;
                break;

            case AgentStateChanged stateChanged:
                _bridge?.PublishAgentStateChanged(stateChanged.AgentId, stateChanged.State);
                break;

            case AgentSpawned spawned:
                _bridge?.PublishAgentSpawned(spawned.AgentId, new AgentVisualInfo(spawned.AgentId, spawned.AgentId));
                break;

            case AgentStopped stopped:
                _bridge?.PublishAgentDespawned(stopped.AgentId);
                break;

            case RenderGenUI genui:
                _bridge?.PublishGenUIRequest(genui.AgentId, genui.A2UIJson);
                break;

            case AgentMemoryActivity memory:
                // Could extend IViewportBridge for memory indicators
                break;
        }
    }
}
