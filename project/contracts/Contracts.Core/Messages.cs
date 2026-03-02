namespace GiantIsopod.Contracts.Core;

// ── Agent lifecycle ──

public record SpawnAgent(string AgentId, string AieosProfilePath, string SkillBundleName, string? MemoryFilePath = null, string? RuntimeId = null, ModelSpec? Model = null);
public record AgentSpawned(string AgentId);
public record StopAgent(string AgentId);
public record AgentStopped(string AgentId);

// ── Agent runtime ──

public record StartRuntime(string AgentId);
public record RuntimeStarted(string AgentId, int ProcessId);
public record RuntimeEvent(string AgentId, string RawJson);
public record RuntimeOutput(string AgentId, string Line);
public record RuntimeExited(string AgentId, int ExitCode);
public record SendPrompt(string AgentId, string Message);

// ── Agent state (actor → viewport) ──

public record AgentStateChanged(string AgentId, AgentActivityState State, string? ActiveToolName = null);
public record AgentMemoryActivity(string AgentId, bool IsStoring, string? Title = null);

// ── Task dispatch ──

public record TaskRequest(string TaskId, string Description, IReadOnlySet<string> RequiredCapabilities, string? GraphId = null);
public record TaskRequestWithBudget(
    string TaskId, string Description, IReadOnlySet<string> RequiredCapabilities,
    TaskBudget Budget, string? GraphId = null) : TaskRequest(TaskId, Description, RequiredCapabilities, GraphId);
public record TaskAssigned(string TaskId, string AgentId, string? Description = null, TaskBudget? Budget = null, string? GraphId = null);
public record TaskCompleted(string TaskId, string AgentId, bool Success, string? Summary = null, string? GraphId = null, IReadOnlyList<ArtifactRef>? Artifacts = null, ProposedSubplan? Subplan = null);
public record TaskFailed(string TaskId, string? Reason = null, IReadOnlySet<string>? UnmetCapabilities = null, string? GraphId = null);
public record TaskTimedOut(string TaskId);

// ── Task budget ──

public record TaskBudget(
    TimeSpan? Deadline = null,
    int? MaxTokens = null,
    RiskLevel Risk = RiskLevel.Normal);

public enum RiskLevel { Low, Normal, High, Critical }

public record TaskBudgetReport(
    string TaskId, string AgentId,
    TimeSpan Elapsed, int EstimatedTokensUsed, RiskLevel Risk,
    bool DeadlineExceeded, bool TokenBudgetExceeded);

public record RiskApprovalRequired(string TaskId, RiskLevel Risk, string Description);
public record RiskApproved(string TaskId);
public record RiskDenied(string TaskId, string Reason);

// ── Task graph (DAG) ──

public record TaskNode(string TaskId, string Description, IReadOnlySet<string> RequiredCapabilities, TaskBudget? Budget = null);
public record TaskEdge(string FromTaskId, string ToTaskId);
public record SubmitTaskGraph(string GraphId, IReadOnlyList<TaskNode> Nodes, IReadOnlyList<TaskEdge> Edges, TaskBudget? GraphBudget = null);
public record TaskGraphAccepted(string GraphId, int NodeCount, int EdgeCount);
public record TaskGraphRejected(string GraphId, string Reason);
public record TaskReadyForDispatch(string GraphId, string TaskId, string Description, IReadOnlySet<string> RequiredCapabilities, TaskBudget? Budget = null);
public record TaskNodeCompleted(string GraphId, string TaskId, bool Success, string? Summary = null);
public record TaskGraphCompleted(string GraphId, IReadOnlyDictionary<string, bool> Results);

public enum TaskNodeStatus { Pending, Ready, Dispatched, Completed, Failed, Cancelled, WaitingForSubtasks, Synthesizing }

// ── Task graph viewport notifications ──

public record NotifyTaskGraphSubmitted(string GraphId, IReadOnlyList<TaskNode> Nodes, IReadOnlyList<TaskEdge> Edges);
public record NotifyTaskNodeStatusChanged(string GraphId, string TaskId, TaskNodeStatus Status, string? AgentId = null);

// ── Market coordination ──

public record TaskAvailable(string TaskId, string Description, IReadOnlySet<string> RequiredCapabilities, TimeSpan BidWindow);
public record TaskBid(string TaskId, string AgentId, double Fitness, int ActiveTaskCount, TimeSpan EstimatedDuration, int EstimatedTokens = 0);
public record TaskAwardedTo(string TaskId, string AgentId);
public record TaskBidRejected(string TaskId, string AgentId);

// ── Skill registry ──

public record RegisterSkills(string AgentId, IReadOnlySet<string> Capabilities);
public record UnregisterSkills(string AgentId);
public record QueryCapableAgents(IReadOnlySet<string> RequiredCapabilities);
public record CapableAgentsResult(IReadOnlyList<string> AgentIds);

// ── Blackboard (shared memory) ──

public record PublishSignal(string Key, string Value, string? PublisherId = null);
public record QuerySignal(string Key);
public record SignalValue(string Key, string? Value, string? PublisherId = null);
public record SubscribeSignal(string Key);
public record ListSignals(string? KeyPrefix = null);
public record SignalList(IReadOnlyList<string> Keys);

// ── Working memory (per-agent) ──

public record SetWorkingMemory(string AgentId, string Key, string Value);
public record GetWorkingMemory(string AgentId, string Key);
public record WorkingMemoryValue(string AgentId, string Key, string? Value);
public record ClearWorkingMemory(string AgentId);

// ── Episodic memory (per-agent, per-task run via Memvid) ──

public record StoreMemory(string AgentId, string TaskRunId, string Content, string? Title = null, IDictionary<string, string>? Tags = null);
public record SearchMemory(string AgentId, string Query, string? TaskRunId = null, int TopK = 10);
public record MemorySearchResult(string AgentId, string? TaskRunId, IReadOnlyList<MemoryHit> Hits);

// ── Long-term knowledge (per-agent, persistent, embedded DB — future) ──

public record StoreKnowledge(string AgentId, string Content, string Category, IDictionary<string, string>? Tags = null);
public record QueryKnowledge(string AgentId, string Query, string? Category = null, int TopK = 10);
public record KnowledgeResult(string AgentId, IReadOnlyList<KnowledgeEntry> Entries);
public record KnowledgeEntry(string Content, string Category, double Relevance, IDictionary<string, string>? Tags, DateTimeOffset StoredAt);

// ── Episodic memory commit ──

public record CommitMemory(string AgentId);

// ── GenUI (A2UI render requests) ──

public record RenderGenUI(string AgentId, string A2UIJson);

// ── GenUI actions (UI → agent) ──

public record GenUIAction(string AgentId, string SurfaceId, string ActionId, string ComponentId, IReadOnlyDictionary<string, string>? Payload = null);

// ── AG-UI events (agent → viewport) ──

public record AgUiEvent(string AgentId, object Event);

// ── A2A (Agent-to-Agent) ──

public record A2ASendTask(string TaskId, string TargetAgentId, string Description, string? PayloadJson = null);
public record A2AGetTask(string TaskId);
public record A2ATaskResult(string TaskId, string StatusJson);
public record QueryAgentCards(IReadOnlySet<string>? RequiredCapabilities = null);
public record AgentCardsResult(IReadOnlyList<AgentCardInfo> Cards);
public record AgentCardInfo(string AgentId, string Name, string? Description = null, IReadOnlyList<string>? Skills = null);

// ── Artifact registry ──

public enum ArtifactType { Code, Doc, Image, Audio, Model3D, App, Dataset, Config }

public record ArtifactProvenance(
    string TaskId,
    string AgentId,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string>? InputArtifactIds = null);

public record ValidatorResult(string ValidatorName, bool Passed, string? Details = null);

public record ArtifactRef(
    string ArtifactId,
    ArtifactType Type,
    string Format,
    string Uri,
    string? ContentHash,
    ArtifactProvenance Provenance,
    IReadOnlyDictionary<string, string>? Metadata = null,
    IReadOnlyList<ValidatorResult>? Validators = null);

public record RegisterArtifact(ArtifactRef Artifact);
public record ArtifactRegistered(string ArtifactId);

public record GetArtifact(string ArtifactId);
public record GetArtifactsByTask(string TaskId);
public record GetArtifactsByType(ArtifactType Type);
public record ArtifactResult(ArtifactRef? Artifact);
public record ArtifactListResult(IReadOnlyList<ArtifactRef> Artifacts);

public record UpdateValidation(string ArtifactId, ValidatorResult Result);
public record ArtifactValidationUpdated(string ArtifactId);

public record BlessArtifact(string ArtifactId);
public record ArtifactBlessed(string ArtifactId);

// ── Progressive decomposition (ADR-009) ──

public enum DecompositionReason { TooLarge, MissingInfo, DependencyDiscovered, Ambiguity, ExternalToolRequired }
public enum StopKind { AllSubtasksComplete, FirstSuccess, UserDecision }

public record StopCondition(StopKind Kind, string Description);

public record SubtaskProposal(
    string Description,
    IReadOnlySet<string> RequiredCapabilities,
    IReadOnlyList<string> DependsOnSubtasks,
    TimeSpan? BudgetCap = null,
    IReadOnlyList<ArtifactType>? ExpectedOutputTypes = null);

public record ProposedSubplan(
    string ParentTaskId,
    DecompositionReason Reason,
    IReadOnlyList<SubtaskProposal> Subtasks,
    StopCondition? StopWhen = null);

public record TaskDecompositionAccepted(string ParentTaskId, IReadOnlyList<string> SubtaskIds, string? GraphId = null);
public record TaskDecompositionRejected(string ParentTaskId, string Reason, string? GraphId = null);
public record SubtasksCompleted(string ParentTaskId, IReadOnlyList<TaskCompleted> Results, string? GraphId = null);

// ── Viewport bridge ──

public record SetViewportBridge(IViewportBridge Bridge);

// ── Agent spawn with visual info (actor → viewport) ──

public record AgentSpawnedWithVisuals(string AgentId, AgentVisualInfo VisualInfo);
