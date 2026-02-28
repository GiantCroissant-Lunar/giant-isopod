using Godot;
using GiantIsopod.Contracts.Core;

namespace GiantIsopod.Hosts.CompleteApp;

/// <summary>
/// Implements IViewportBridge for Godot. Receives agent state events
/// from the Akka.NET actor system and enqueues them for ECS processing.
/// Thread-safe — called from actor threads, consumed on Godot main thread.
/// </summary>
public sealed class GodotViewportBridge : IViewportBridge
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<ViewportEvent> _eventQueue = new();

    public void PublishAgentStateChanged(string agentId, AgentActivityState state)
    {
        _eventQueue.Enqueue(new StateChangedEvent(agentId, state));
    }

    public void PublishAgentSpawned(string agentId, AgentVisualInfo visualInfo)
    {
        _eventQueue.Enqueue(new AgentSpawnedEvent(agentId, visualInfo));
    }

    public void PublishAgentDespawned(string agentId)
    {
        _eventQueue.Enqueue(new AgentDespawnedEvent(agentId));
    }

    public void PublishGenUIRequest(string agentId, string a2uiJson)
    {
        _eventQueue.Enqueue(new GenUIRequestEvent(agentId, a2uiJson));
    }

    public void PublishProcessStarted(string agentId, int processId)
    {
        _eventQueue.Enqueue(new ProcessStartedEvent(agentId, processId));
    }

    public void PublishProcessExited(string agentId, int exitCode)
    {
        _eventQueue.Enqueue(new ProcessExitedEvent(agentId, exitCode));
    }

    /// <summary>
    /// Called from Godot _Process to drain events on the main thread.
    /// </summary>
    public IEnumerable<ViewportEvent> DrainEvents()
    {
        while (_eventQueue.TryDequeue(out var evt))
            yield return evt;
    }
}

public abstract record ViewportEvent(string AgentId);
public record StateChangedEvent(string AgentId, AgentActivityState State) : ViewportEvent(AgentId);
public record AgentSpawnedEvent(string AgentId, AgentVisualInfo VisualInfo) : ViewportEvent(AgentId);
public record AgentDespawnedEvent(string AgentId) : ViewportEvent(AgentId);
public record GenUIRequestEvent(string AgentId, string A2UIJson) : ViewportEvent(AgentId);
public record ProcessStartedEvent(string AgentId, int ProcessId) : ViewportEvent(AgentId);
public record ProcessExitedEvent(string AgentId, int ExitCode) : ViewportEvent(AgentId);
