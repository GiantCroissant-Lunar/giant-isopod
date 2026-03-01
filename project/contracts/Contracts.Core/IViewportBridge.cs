namespace GiantIsopod.Contracts.Core;

/// <summary>
/// Observer bridge from actor system to Godot viewport.
/// Pushes agent state changes for ECS rendering. Read-only — never commands agents.
/// </summary>
public interface IViewportBridge
{
    void PublishAgentStateChanged(string agentId, AgentActivityState state);
    void PublishAgentSpawned(string agentId, AgentVisualInfo visualInfo);
    void PublishAgentDespawned(string agentId);
    void PublishGenUIRequest(string agentId, string a2uiJson);
    void PublishRuntimeStarted(string agentId, int processId);
    void PublishRuntimeExited(string agentId, int exitCode);
    void PublishRuntimeOutput(string agentId, string line);

    // Task graph visualization (default no-op for non-Godot bridges)
    void PublishTaskGraphSubmitted(string graphId, IReadOnlyList<TaskNode> nodes, IReadOnlyList<TaskEdge> edges) { }
    void PublishTaskNodeStatusChanged(string graphId, string taskId, TaskNodeStatus status, string? agentId = null) { }
    void PublishTaskGraphCompleted(string graphId, IReadOnlyDictionary<string, bool> results) { }
}

public enum AgentActivityState
{
    Idle,
    Walking,
    Typing,
    Reading,
    Waiting,
    Thinking
}

public record AgentVisualInfo(
    string AgentId,
    string DisplayName,
    string? SkinTone = null,
    string? HairStyle = null,
    string? HairColor = null,
    string? AestheticArchetype = null
);
