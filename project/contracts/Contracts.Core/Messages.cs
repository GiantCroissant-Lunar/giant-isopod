namespace GiantIsopod.Contracts.Core;

// ── Agent lifecycle ──

public record SpawnAgent(string AgentId, string AieosProfilePath, string SkillBundleName, string? MemoryFilePath = null);
public record AgentSpawned(string AgentId);
public record StopAgent(string AgentId);
public record AgentStopped(string AgentId);

// ── Agent process ──

public record StartProcess(string AgentId);
public record ProcessStarted(string AgentId, int ProcessId);
public record ProcessEvent(string AgentId, string RawJson);
public record ProcessExited(string AgentId, int ExitCode);
public record SendPrompt(string AgentId, string Message);

// ── Agent state (actor → viewport) ──

public record AgentStateChanged(string AgentId, AgentActivityState State, string? ActiveToolName = null);
public record AgentMemoryActivity(string AgentId, bool IsStoring, string? Title = null);

// ── Task dispatch ──

public record TaskRequest(string TaskId, string Description, IReadOnlySet<string> RequiredCapabilities);
public record TaskAssigned(string TaskId, string AgentId);
public record TaskCompleted(string TaskId, string AgentId, bool Success, string? Summary = null);
public record TaskFailed(string TaskId, string? Reason = null, IReadOnlySet<string>? UnmetCapabilities = null);

// ── Skill registry ──

public record RegisterSkills(string AgentId, IReadOnlySet<string> Capabilities);
public record UnregisterSkills(string AgentId);
public record QueryCapableAgents(IReadOnlySet<string> RequiredCapabilities);
public record CapableAgentsResult(IReadOnlyList<string> AgentIds);

// ── Memory ──

public record StoreMemory(string AgentId, string Content, string? Title = null, IDictionary<string, string>? Tags = null);
public record SearchMemory(string AgentId, string Query, int TopK = 10);
public record MemorySearchResult(string AgentId, IReadOnlyList<MemoryHit> Hits);

// ── GenUI (A2UI render requests) ──

public record RenderGenUI(string AgentId, string A2UIJson);
