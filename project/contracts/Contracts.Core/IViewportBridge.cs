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
    void PublishProcessStarted(string agentId, int processId);
    void PublishProcessExited(string agentId, int exitCode);
    void PublishProcessOutput(string agentId, string line);
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
