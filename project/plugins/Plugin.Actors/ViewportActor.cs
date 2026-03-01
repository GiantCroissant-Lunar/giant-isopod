using Akka.Actor;
using GiantIsopod.Contracts.Core;
using Microsoft.Extensions.Logging;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// /user/viewport — observer bridge to Godot. Receives agent state events
/// and pushes them to the ECS via a thread-safe queue. Never commands agents.
/// </summary>
public sealed class ViewportActor : UntypedActor
{
    private readonly ILogger<ViewportActor> _logger;
    private IViewportBridge? _bridge;

    public ViewportActor(ILogger<ViewportActor> logger)
    {
        _logger = logger;
    }

    protected override void OnReceive(object message)
    {
        switch (message)
        {
            case SetViewportBridge setBridge:
                _bridge = setBridge.Bridge;
                _logger.LogDebug("Viewport bridge connected");
                break;

            case AgentStateChanged stateChanged:
                _bridge?.PublishAgentStateChanged(stateChanged.AgentId, stateChanged.State);
                break;

            case AgentSpawned spawned:
                _bridge?.PublishAgentSpawned(spawned.AgentId, new AgentVisualInfo(spawned.AgentId, spawned.AgentId));
                break;

            case AgentSpawnedWithVisuals spawnedVisuals:
                _bridge?.PublishAgentSpawned(spawnedVisuals.AgentId, spawnedVisuals.VisualInfo);
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

            case ProcessStarted started:
                _bridge?.PublishProcessStarted(started.AgentId, started.ProcessId);
                break;

            case ProcessExited exited:
                _bridge?.PublishProcessExited(exited.AgentId, exited.ExitCode);
                break;

            case ProcessOutput output:
                _bridge?.PublishProcessOutput(output.AgentId, output.Line);
                break;

            case NotifyTaskGraphSubmitted submitted:
                _bridge?.PublishTaskGraphSubmitted(submitted.GraphId, submitted.Nodes, submitted.Edges);
                break;

            case NotifyTaskNodeStatusChanged statusChanged:
                _bridge?.PublishTaskNodeStatusChanged(statusChanged.GraphId, statusChanged.TaskId, statusChanged.Status, statusChanged.AgentId);
                break;

            case TaskGraphCompleted graphCompleted:
                _bridge?.PublishTaskGraphCompleted(graphCompleted.GraphId, graphCompleted.Results);
                break;
        }
    }
}
