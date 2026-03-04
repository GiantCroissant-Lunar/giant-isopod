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

public record TaskRequest(
    string TaskId,
    string Description,
    IReadOnlySet<string> RequiredCapabilities,
    string? GraphId = null,
    string? PreferredRuntimeId = null,
    IReadOnlyList<string>? OwnedPaths = null,
    IReadOnlyList<string>? ExpectedFiles = null,
    bool AllowNoOpCompletion = false);
public record TaskRequestWithBudget(
    string TaskId, string Description, IReadOnlySet<string> RequiredCapabilities,
    TaskBudget Budget, string? GraphId = null, string? PreferredRuntimeId = null,
    IReadOnlyList<string>? OwnedPaths = null, IReadOnlyList<string>? ExpectedFiles = null, bool AllowNoOpCompletion = false)
    : TaskRequest(TaskId, Description, RequiredCapabilities, GraphId, PreferredRuntimeId, OwnedPaths, ExpectedFiles, AllowNoOpCompletion);
public record TaskAssigned(
    string TaskId,
    string AgentId,
    string? Description = null,
    TaskBudget? Budget = null,
    string? GraphId = null,
    string? WorkspacePath = null,
    IReadOnlyList<string>? OwnedPaths = null,
    IReadOnlyList<string>? ExpectedFiles = null,
    IReadOnlySet<string>? RequiredCapabilities = null,
    bool AllowNoOpCompletion = false);
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

public record TaskNode(
    string TaskId,
    string Description,
    IReadOnlySet<string> RequiredCapabilities,
    TaskBudget? Budget = null,
    IReadOnlyList<string>? RequiredValidators = null,
    int MaxValidationAttempts = 2,
    string? PreferredRuntimeId = null,
    IReadOnlySet<string>? PlannerRequiredCapabilities = null,
    string? PreferredPlannerRuntimeId = null,
    IReadOnlyList<string>? OwnedPaths = null,
    IReadOnlyList<string>? ExpectedFiles = null,
    bool AllowNoOpCompletion = false);
public record TaskEdge(string FromTaskId, string ToTaskId);
public record SubmitTaskGraph(string GraphId, IReadOnlyList<TaskNode> Nodes, IReadOnlyList<TaskEdge> Edges, TaskBudget? GraphBudget = null);
public record TaskGraphAccepted(string GraphId, int NodeCount, int EdgeCount);
public record TaskGraphRejected(string GraphId, string Reason);
public record TaskReadyForDispatch(
    string GraphId,
    string TaskId,
    string Description,
    IReadOnlySet<string> RequiredCapabilities,
    TaskBudget? Budget = null,
    string? PreferredRuntimeId = null,
    IReadOnlyList<string>? OwnedPaths = null,
    IReadOnlyList<string>? ExpectedFiles = null,
    bool AllowNoOpCompletion = false);
public record TaskNodeCompleted(string GraphId, string TaskId, bool Success, string? Summary = null);
public record TaskGraphCompleted(string GraphId, IReadOnlyDictionary<string, bool> Results);

public enum TaskNodeStatus { Pending, Ready, Planning, Dispatched, Completed, Failed, Cancelled, WaitingForSubtasks, Synthesizing, Validating }

// ── Task graph viewport notifications ──

public record NotifyTaskGraphSubmitted(string GraphId, IReadOnlyList<TaskNode> Nodes, IReadOnlyList<TaskEdge> Edges);
public record NotifyTaskNodeStatusChanged(string GraphId, string TaskId, TaskNodeStatus Status, string? AgentId = null);

// ── Market coordination ──

public record TaskAvailable(
    string TaskId,
    string Description,
    IReadOnlySet<string> RequiredCapabilities,
    TimeSpan BidWindow,
    string? PreferredRuntimeId = null);
public record TaskBid(
    string TaskId,
    string AgentId,
    double Fitness,
    int ActiveTaskCount,
    TimeSpan EstimatedDuration,
    int EstimatedTokens = 0,
    string? RuntimeId = null);
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

public record ArtifactFollowupSuggestion(
    string SuggestionId,
    string ArtifactId,
    string Title,
    string Description,
    IReadOnlySet<string> RequiredCapabilities,
    IReadOnlyList<string>? OwnedPaths = null,
    IReadOnlyList<string>? ExpectedFiles = null,
    IReadOnlyList<string>? RequiredValidators = null,
    string? PreferredRuntimeId = null);

public record ArtifactFollowupSuggested(string ArtifactId, IReadOnlyList<ArtifactFollowupSuggestion> Suggestions);
public record GetArtifactFollowups(string ArtifactId);
public record ArtifactFollowupResult(string ArtifactId, IReadOnlyList<ArtifactFollowupSuggestion> Suggestions);
public record SubmitArtifactFollowup(string ArtifactId, IReadOnlyList<string>? SuggestionIds = null, string? GraphId = null);
public record ArtifactFollowupSubmitted(string ArtifactId, string GraphId, IReadOnlyList<string> TaskIds);

// ── Progressive decomposition (ADR-009) ──

public enum DecompositionReason { TooLarge, MissingInfo, DependencyDiscovered, Ambiguity, ExternalToolRequired }
public enum StopKind { AllSubtasksComplete, FirstSuccess, UserDecision }

public record StopCondition(StopKind Kind, string Description);

public record SubtaskProposal(
    string Description,
    IReadOnlySet<string> RequiredCapabilities,
    IReadOnlyList<string> DependsOnSubtasks,
    TimeSpan? BudgetCap = null,
    IReadOnlyList<ArtifactType>? ExpectedOutputTypes = null,
    string? PreferredRuntimeId = null,
    IReadOnlyList<string>? OwnedPaths = null,
    IReadOnlyList<string>? ExpectedFiles = null,
    bool AllowNoOpCompletion = false);

public record ProposedSubplan(
    string ParentTaskId,
    DecompositionReason Reason,
    IReadOnlyList<SubtaskProposal> Subtasks,
    StopCondition? StopWhen = null);

public record TaskDecompositionAccepted(string ParentTaskId, IReadOnlyList<string> SubtaskIds, string? GraphId = null);
public record TaskDecompositionRejected(string ParentTaskId, string Reason, string? GraphId = null);
public record SubtasksCompleted(string ParentTaskId, IReadOnlyList<TaskCompleted> Results, string? GraphId = null);

// ── Validator framework (ADR-011) ──

public enum ValidatorKind { Script, AgentReview }

public record ValidatorSpec(
    string Name, ValidatorKind Kind, ArtifactType AppliesTo,
    string Command, string? Rubric = null,
    IReadOnlyDictionary<string, string>? Config = null);

public record RegisterValidator(ValidatorSpec Spec);
public record ValidatorRegistered(string Name);

public record ValidateArtifact(
    string ArtifactId, ArtifactRef Artifact,
    string? TaskId = null,
    IReadOnlyList<string>? RequiredValidators = null,
    IReadOnlyList<string>? OwnedPaths = null,
    IReadOnlyList<string>? ExpectedFiles = null);

public record ValidationComplete(
    string ArtifactId, IReadOnlyList<ValidatorResult> Results, string? TaskId = null);

public record RevisionRequested(
    string TaskId, string ArtifactId,
    IReadOnlyList<ValidatorResult> Failures, int AttemptNumber);

// ── Workspace lifecycle (ADR-010) ──

public enum WorkspaceStatus { Active, Committed, Merged, Released }

public record Workspace(
    string TaskId,
    string WorktreePath,
    string BranchName,
    string BaseRef,
    WorkspaceStatus Status);

public record AllocateWorkspace(string TaskId, string BaseRef);
public record WorkspaceAllocated(string TaskId, string WorktreePath, string BranchName);
public record AllocationFailed(string TaskId, string Reason);

public record ReleaseWorkspace(string TaskId);
public record WorkspaceReleased(string TaskId);

public record RequestMerge(string TaskId);
public record MergeSucceeded(string TaskId, string MergeCommitSha);
public record MergeConflict(string TaskId, IReadOnlyList<string> ConflictingFiles);

// ── Viewport bridge ──

public record SetViewportBridge(IViewportBridge Bridge);

// ── Agent spawn with visual info (actor → viewport) ──

public record AgentSpawnedWithVisuals(string AgentId, AgentVisualInfo VisualInfo);
