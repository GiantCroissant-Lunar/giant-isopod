using Akka.Actor;
using GiantIsopod.Contracts.Core;
using Microsoft.Extensions.Logging;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// /user/agents/{name} — owns an agent's runtime, skill registration, and memory reference.
/// Child actors: rpc (runtime pipe), tasks (task lifecycle).
/// Loads AIEOS profile for visual identity. Runs a demo activity cycle when runtime is not connected.
/// </summary>
public sealed class AgentActor : UntypedActor
{
    private readonly string _agentId;
    private readonly string _aieosProfilePath;
    private readonly string _skillBundleName;
    private readonly string? _memoryFilePath;
    private readonly string? _runtimeId;
    private readonly ModelSpec? _model;
    private readonly IActorRef _registry;
    private readonly IActorRef _artifactRegistry;
    private readonly IActorRef _memorySupervisor;
    private readonly IActorRef _knowledgeSupervisor;
    private readonly AgentWorldConfig _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AgentActor> _logger;
    private readonly GiantIsopod.Plugin.Mapping.ProtocolMapper _mapper = new();
    private readonly GiantIsopod.Plugin.Protocol.AgUiAdapter _agUiAdapter;

    private IActorRef? _rpcActor;
    private bool _runtimeConnected;
    private ICancelable? _demoTimer;

    private HashSet<string> _capabilities = new();
    private readonly Dictionary<string, string> _workingMemory = new();
    private readonly Dictionary<string, ActiveTaskContext> _activeTasks = new();
    private readonly Dictionary<string, ActiveTaskContext> _plannerParentTasks = new();
    private int _activeTaskCount;
    private const int MaxConcurrentTasks = 1;
    private const double MinBidThreshold = 0.5;
    private static readonly TimeSpan RetrievalTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ArtifactRegistrationTimeout = TimeSpan.FromSeconds(5);

    private static readonly AgentActivityState[] DemoStates =
    [
        AgentActivityState.Idle,
        AgentActivityState.Thinking,
        AgentActivityState.Typing,
        AgentActivityState.Reading,
        AgentActivityState.Idle,
        AgentActivityState.Waiting,
    ];

    public AgentActor(
        string agentId,
        string aieosProfilePath,
        string skillBundleName,
        string? memoryFilePath,
        IActorRef registry,
        IActorRef artifactRegistry,
        IActorRef memorySupervisor,
        IActorRef knowledgeSupervisor,
        AgentWorldConfig config,
        ILoggerFactory loggerFactory,
        string? runtimeId = null,
        ModelSpec? model = null)
    {
        _agentId = agentId;
        _aieosProfilePath = aieosProfilePath;
        _skillBundleName = skillBundleName;
        _memoryFilePath = memoryFilePath;
        _runtimeId = runtimeId;
        _model = model;
        _registry = registry;
        _artifactRegistry = artifactRegistry;
        _memorySupervisor = memorySupervisor;
        _knowledgeSupervisor = knowledgeSupervisor;
        _config = config;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<AgentActor>();
        _agUiAdapter = new GiantIsopod.Plugin.Protocol.AgUiAdapter(agentId);
    }

    protected override void PreStart()
    {
        _rpcActor = Context.ActorOf(
            Props.Create(() => new AgentRuntimeActor(_agentId, _config, _runtimeId, _model)),
            "rpc");

        Context.ActorOf(
            Props.Create(() => new AgentTaskActor(_agentId,
                _loggerFactory.CreateLogger<AgentTaskActor>())),
            "tasks");

        var visualInfo = LoadVisualInfo();
        var capabilities = LoadCapabilities();

        _capabilities = capabilities;
        _registry.Tell(new RegisterSkills(_agentId, capabilities));

        Context.System.ActorSelection("/user/viewport")
            .Tell(new AgentSpawnedWithVisuals(_agentId, visualInfo));

        if (_memoryFilePath != null)
            _memorySupervisor.Tell(new StoreMemory(_agentId, "session", $"Agent {_agentId} started", "session_start"));

        _rpcActor.Tell(new StartRuntime(_agentId));
        _logger.LogInformation("Agent {AgentId} started (bundle: {Bundle})", _agentId, _skillBundleName);

        StartDemoTimer();
    }

    protected override void PostStop()
    {
        _demoTimer?.Cancel();
        _registry.Tell(new UnregisterSkills(_agentId));
        _logger.LogInformation("Agent {AgentId} stopped", _agentId);
    }

    protected override void OnReceive(object message)
    {
        switch (message)
        {
            case SendPrompt prompt:
                _rpcActor?.Forward(prompt);
                break;

            case RuntimeStarted started when started.ProcessId > 0:
                _runtimeConnected = true;
                _demoTimer?.Cancel();
                Context.System.ActorSelection("/user/viewport")
                    .Tell(new RuntimeStarted(_agentId, started.ProcessId));
                _logger.LogInformation("Agent {AgentId} runtime connected (pid: {Pid})", _agentId, started.ProcessId);
                break;

            case RuntimeEvent evt:
                Context.System.ActorSelection("/user/viewport")
                    .Tell(new RuntimeOutput(_agentId, evt.RawJson));

                var activityState = MapTextToState(evt.RawJson);
                if (activityState != AgentActivityState.Idle)
                {
                    Context.System.ActorSelection("/user/viewport")
                        .Tell(new AgentStateChanged(_agentId, activityState));
                }

                var agUiEvents = _agUiAdapter.MapRpcEventToAgUiEvents(evt.RawJson);
                foreach (var agUiEvt in agUiEvents)
                {
                    Context.System.ActorSelection("/user/viewport")
                        .Tell(new AgUiEvent(_agentId, agUiEvt));
                }
                break;

            case RuntimeExited exited:
                _runtimeConnected = false;
                _logger.LogWarning("Agent {AgentId} runtime exited (code: {Code})", _agentId, exited.ExitCode);
                Context.System.ActorSelection("/user/viewport")
                    .Tell(new AgentStateChanged(_agentId, AgentActivityState.Idle));
                Context.System.ActorSelection("/user/viewport")
                    .Tell(new RuntimeExited(_agentId, exited.ExitCode));
                StartDemoTimer();
                break;

            case TaskAvailable available:
                EvaluateAndBid(available);
                break;

            case TaskAssigned task:
                _activeTaskCount++;
                _activeTasks[task.TaskId] = new ActiveTaskContext(task.TaskId, task.GraphId, task.WorkspacePath, task.AllowNoOpCompletion);
                if (TryGetPlannerParentTaskId(task.TaskId, out var plannerParentTaskId))
                {
                    _plannerParentTasks[plannerParentTaskId] =
                        new ActiveTaskContext(plannerParentTaskId, task.GraphId, task.WorkspacePath, task.AllowNoOpCompletion);
                }

                if (task.Budget?.MaxTokens is { } maxTokens)
                    _rpcActor?.Tell(new SetTokenBudget(task.TaskId, maxTokens));

                if (task.Description != null)
                {
                    Context.System.ActorSelection("/user/viewport")
                        .Tell(new AgentMemoryActivity(_agentId, false, "Retrieving context"));
                    _knowledgeSupervisor.Ask<KnowledgeResult>(
                            new QueryKnowledge(_agentId, task.Description, TopK: 5),
                            RetrievalTimeout)
                        .ContinueWith(t =>
                        {
                            if (t.IsFaulted || t.IsCanceled)
                                return (object)new RetrievalFailed(task, t.Exception?.GetBaseException()?.Message ?? "timeout");
                            return new RetrievalComplete(task, t.Result.Entries);
                        })
                        .PipeTo(Self);
                }
                else
                {
                    Context.Child("tasks").Tell(task);
                    StartTaskExecution(task, BuildFallbackPrompt(task));
                }
                break;

            case RetrievalComplete retrieval:
                var taskPrompt = retrieval.Entries.Count > 0
                    ? PromptBuilder.BuildTaskPrompt(
                        retrieval.Task.TaskId,
                        retrieval.Task.Description!,
                        retrieval.Entries,
                        retrieval.Task.OwnedPaths,
                        retrieval.Task.ExpectedFiles,
                        retrieval.Task.AllowNoOpCompletion)
                    : BuildFallbackPrompt(retrieval.Task);

                if (retrieval.Entries.Count > 0)
                {
                    _logger.LogInformation("Agent {AgentId} retrieved {Count} knowledge entries for task {TaskId}",
                        _agentId, retrieval.Entries.Count, retrieval.Task.TaskId);
                }

                Context.System.ActorSelection("/user/viewport")
                    .Tell(new AgentMemoryActivity(_agentId, false));
                Context.Child("tasks").Tell(retrieval.Task);
                StartTaskExecution(retrieval.Task, taskPrompt);
                break;

            case RetrievalFailed failed:
                _logger.LogWarning("Knowledge retrieval failed for agent {AgentId} task {TaskId}: {Reason}",
                    _agentId, failed.Task.TaskId, failed.Reason);
                Context.System.ActorSelection("/user/viewport")
                    .Tell(new AgentMemoryActivity(_agentId, false));
                Context.Child("tasks").Tell(failed.Task);
                StartTaskExecution(failed.Task, BuildFallbackPrompt(failed.Task));
                break;

            case TaskCompleted completed:
                if (!Sender.Equals(Context.Child("tasks")))
                {
                    Context.Child("tasks").Tell(completed);
                    break;
                }

                _activeTaskCount = Math.Max(0, _activeTaskCount - 1);
                _activeTasks.Remove(completed.TaskId);
                if (!TryGetPlannerParentTaskId(completed.TaskId, out _))
                    _plannerParentTasks.Remove(completed.TaskId);
                Context.System.ActorSelection("/user/dispatch")
                    .Tell(new AgentCapacityAvailable(_agentId));
                if (completed.Artifacts is { Count: > 0 })
                    RegisterArtifacts(completed);
                else
                    FinalizeCompletedTask(completed);
                break;

            case RegisteredTaskCompleted registered:
                FinalizeCompletedTask(registered.Completed);
                break;

            case ArtifactRegistrationFailed failedRegistration:
                _logger.LogWarning("Artifact registration failed for task {TaskId}: {Reason}",
                    failedRegistration.Completed.TaskId, failedRegistration.Reason);
                FinalizeCompletedTask(failedRegistration.Completed);
                break;

            case TaskFailed failed:
                if (!Sender.Equals(Context.Child("tasks")))
                {
                    Context.Child("tasks").Tell(failed);
                    break;
                }

                _activeTaskCount = Math.Max(0, _activeTaskCount - 1);
                _activeTasks.Remove(failed.TaskId);
                if (!TryGetPlannerParentTaskId(failed.TaskId, out _))
                    _plannerParentTasks.Remove(failed.TaskId);
                Context.System.ActorSelection("/user/dispatch")
                    .Tell(new AgentCapacityAvailable(_agentId));
                if (failed.GraphId != null)
                    Context.System.ActorSelection("/user/taskgraph").Tell(failed);
                if (failed.Reason != null)
                {
                    _knowledgeSupervisor.Tell(new StoreKnowledge(
                        _agentId, failed.Reason, "pitfall",
                        new Dictionary<string, string> { ["taskId"] = failed.TaskId }));
                }
                break;

            case SubtasksCompleted subtasksCompleted:
                if (TryGetSynthesisContext(subtasksCompleted.ParentTaskId, out var synthesisTask))
                {
                    var synthesisPrompt = PromptBuilder.BuildSynthesisPrompt(
                        subtasksCompleted.ParentTaskId, subtasksCompleted.Results);
                    _rpcActor?.Tell(new ExecuteTaskPrompt(
                        _agentId,
                        subtasksCompleted.ParentTaskId,
                        synthesisPrompt,
                        synthesisTask.GraphId,
                        synthesisTask.WorkspacePath,
                        synthesisTask.AllowNoOpCompletion,
                        CollectArtifacts: false));
                    _logger.LogInformation("Agent {AgentId} received {Count} subtask results for synthesis of {TaskId}",
                        _agentId, subtasksCompleted.Results.Count, subtasksCompleted.ParentTaskId);
                }
                break;

            case TaskDecompositionAccepted accepted:
                if (_plannerParentTasks.TryGetValue(accepted.ParentTaskId, out var plannerParentTask))
                    _activeTasks[accepted.ParentTaskId] = plannerParentTask;
                _logger.LogInformation("Agent {AgentId} decomposition accepted for {TaskId}: {Count} subtasks",
                    _agentId, accepted.ParentTaskId, accepted.SubtaskIds.Count);
                break;

            case TaskDecompositionRejected rejected:
                _logger.LogWarning("Agent {AgentId} decomposition rejected for {TaskId}: {Reason}",
                    _agentId, rejected.ParentTaskId, rejected.Reason);
                if (_plannerParentTasks.TryGetValue(rejected.ParentTaskId, out var plannerRejectedTask))
                    _activeTasks[rejected.ParentTaskId] = plannerRejectedTask;

                if (_activeTasks.TryGetValue(rejected.ParentTaskId, out var rejectedTask))
                {
                    _rpcActor?.Tell(new ExecuteTaskPrompt(
                        _agentId,
                        rejected.ParentTaskId,
                        $"Your proposed subtask decomposition for task {rejected.ParentTaskId} was rejected: {rejected.Reason}. Please complete the task directly.",
                        rejectedTask.GraphId,
                        rejectedTask.WorkspacePath,
                        rejectedTask.AllowNoOpCompletion));
                }
                break;

            case TaskBidRejected:
                break;

            case SetWorkingMemory set:
                _workingMemory[set.Key] = set.Value;
                break;

            case GetWorkingMemory get:
                _workingMemory.TryGetValue(get.Key, out var val);
                Sender.Tell(new WorkingMemoryValue(_agentId, get.Key, val));
                break;

            case ClearWorkingMemory:
                _workingMemory.Clear();
                break;

            case DemoTick tick:
                if (!_runtimeConnected)
                {
                    var state = DemoStates[tick.Index % DemoStates.Length];
                    Context.System.ActorSelection("/user/viewport")
                        .Tell(new AgentStateChanged(_agentId, state));
                    ScheduleNextDemoTick(tick.Index + 1);
                }
                break;
        }
    }

    private void StartDemoTimer()
    {
        _demoTimer?.Cancel();
        ScheduleNextDemoTick(0);
    }

    private void ScheduleNextDemoTick(int nextIndex)
    {
        var rng = new Random(_agentId.GetHashCode() + nextIndex);
        var delay = TimeSpan.FromSeconds(0.8 + rng.NextDouble() * 1.5);
        _demoTimer = Context.System.Scheduler.ScheduleTellOnceCancelable(
            delay, Self, new DemoTick(nextIndex), Self);
    }

    private void StartTaskExecution(TaskAssigned task, string prompt)
    {
        _rpcActor?.Tell(new ExecuteTaskPrompt(
            _agentId,
            task.TaskId,
            prompt,
            task.GraphId,
            task.WorkspacePath,
            task.AllowNoOpCompletion));
    }

    private void RegisterArtifacts(TaskCompleted completed)
    {
        var artifacts = completed.Artifacts ?? Array.Empty<ArtifactRef>();
        Task.Run(async () =>
        {
            foreach (var artifact in artifacts)
            {
                await _artifactRegistry.Ask<ArtifactRegistered>(
                    new RegisterArtifact(artifact),
                    ArtifactRegistrationTimeout);
            }

            return (object)new RegisteredTaskCompleted(completed);
        }).ContinueWith(t =>
        {
            if (t.IsFaulted || t.IsCanceled)
            {
                return (object)new ArtifactRegistrationFailed(
                    completed,
                    t.Exception?.GetBaseException()?.Message ?? "Artifact registration cancelled");
            }

            return t.Result;
        }).PipeTo(Self);
    }

    private void FinalizeCompletedTask(TaskCompleted completed)
    {
        _plannerParentTasks.Remove(completed.TaskId);

        if (completed.GraphId != null)
            Context.System.ActorSelection("/user/taskgraph").Tell(completed);

        if (completed.Summary == null)
            return;

        _logger.LogInformation(
            "Agent {AgentId} finalizing task {TaskId} with summary length {Length}",
            _agentId,
            completed.TaskId,
            completed.Summary.Length);

        _knowledgeSupervisor.Tell(new StoreKnowledge(
            _agentId, completed.Summary, "outcome",
            new Dictionary<string, string> { ["taskId"] = completed.TaskId }));
        _memorySupervisor.Tell(new StoreMemory(
            _agentId, completed.TaskId, completed.Summary,
            $"Task {completed.TaskId} completed"));
        _logger.LogInformation("Agent {AgentId} queued memory store for task {TaskId}", _agentId, completed.TaskId);
    }

    private AgentVisualInfo LoadVisualInfo()
    {
        try
        {
            if (!string.IsNullOrEmpty(_aieosProfilePath))
            {
                var entity = System.Text.Json.JsonSerializer.Deserialize<
                    GiantIsopod.Contracts.Protocol.Aieos.AieosEntity>(_aieosProfilePath);

                if (entity != null)
                    return _mapper.MapAieosToVisualInfo(_agentId, entity);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load AIEOS profile for {AgentId}", _agentId);
        }

        return new AgentVisualInfo(_agentId, _agentId);
    }

    private HashSet<string> LoadCapabilities()
    {
        try
        {
            if (!string.IsNullOrEmpty(_aieosProfilePath))
            {
                var entity = System.Text.Json.JsonSerializer.Deserialize<
                    GiantIsopod.Contracts.Protocol.Aieos.AieosEntity>(_aieosProfilePath);

                if (entity != null)
                    return _mapper.MapAieosToCapabilities(entity);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load capabilities for {AgentId}", _agentId);
        }

        return new HashSet<string>();
    }

    private void EvaluateAndBid(TaskAvailable available)
    {
        if (_activeTaskCount >= MaxConcurrentTasks) return;
        if (available.RequiredCapabilities.Count == 0) return;

        var matchCount = available.RequiredCapabilities.Count(c => _capabilities.Contains(c));
        var fitness = (double)matchCount / available.RequiredCapabilities.Count;
        if (fitness < MinBidThreshold) return;

        var bid = new TaskBid(
            available.TaskId,
            _agentId,
            fitness,
            _activeTaskCount,
            TimeSpan.FromMinutes(5),
            RuntimeId: _runtimeId ?? _config.DefaultRuntimeId);

        Context.System.ActorSelection("/user/dispatch").Tell(bid);
    }

    private static string BuildFallbackPrompt(TaskAssigned task)
    {
        return PromptBuilder.BuildTaskPrompt(
            task.TaskId,
            task.Description ?? $"Complete task {task.TaskId}.",
            ownedPaths: task.OwnedPaths,
            expectedFiles: task.ExpectedFiles,
            allowNoOpCompletion: task.AllowNoOpCompletion);
    }

    private static AgentActivityState MapTextToState(string text)
    {
        if (text.Contains("tool_use") || text.Contains("Writing") || text.Contains("Creating"))
            return AgentActivityState.Typing;
        if (text.Contains("Reading") || text.Contains("read_file") || text.Contains("list_dir"))
            return AgentActivityState.Reading;
        if (text.Contains("Thinking") || text.Contains("thinking"))
            return AgentActivityState.Thinking;
        return AgentActivityState.Idle;
    }

    private bool TryGetSynthesisContext(string parentTaskId, out ActiveTaskContext context)
    {
        if (_activeTasks.TryGetValue(parentTaskId, out context!))
            return true;

        if (_plannerParentTasks.TryGetValue(parentTaskId, out context!))
            return true;

        return false;
    }

    private static bool TryGetPlannerParentTaskId(string taskId, out string parentTaskId)
    {
        const string plannerSuffix = ".__plan";
        if (taskId.EndsWith(plannerSuffix, StringComparison.Ordinal))
        {
            parentTaskId = taskId[..^plannerSuffix.Length];
            return true;
        }

        parentTaskId = string.Empty;
        return false;
    }

    private sealed record RetrievalComplete(TaskAssigned Task, IReadOnlyList<KnowledgeEntry> Entries);
    private sealed record RetrievalFailed(TaskAssigned Task, string Reason);
    private sealed record DemoTick(int Index);
    private sealed record ActiveTaskContext(string TaskId, string? GraphId, string? WorkspacePath, bool AllowNoOpCompletion);
    private sealed record RegisteredTaskCompleted(TaskCompleted Completed);
    private sealed record ArtifactRegistrationFailed(TaskCompleted Completed, string Reason);
}
