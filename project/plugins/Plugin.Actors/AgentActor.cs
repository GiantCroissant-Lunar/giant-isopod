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
    private int _activeTaskCount;
    private const int MaxConcurrentTasks = 3;
    private const double MinBidThreshold = 0.5;
    private static readonly TimeSpan RetrievalTimeout = TimeSpan.FromSeconds(5);

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

        // Load AIEOS profile for visual info and capabilities
        var visualInfo = LoadVisualInfo();
        var capabilities = LoadCapabilities();

        _capabilities = capabilities;
        _registry.Tell(new RegisterSkills(_agentId, capabilities));

        // Notify viewport with rich visual info
        Context.System.ActorSelection("/user/viewport")
            .Tell(new AgentSpawnedWithVisuals(_agentId, visualInfo));

        if (_memoryFilePath != null)
        {
            _memorySupervisor.Tell(new StoreMemory(_agentId, "session", $"Agent {_agentId} started", "session_start"));
        }

        _rpcActor.Tell(new StartRuntime(_agentId));
        _logger.LogInformation("Agent {AgentId} started (bundle: {Bundle})", _agentId, _skillBundleName);

        // Start demo activity cycle (replaced by real runtime events when connected)
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
                // Forward raw text to terminal renderer
                Context.System.ActorSelection("/user/viewport")
                    .Tell(new RuntimeOutput(_agentId, evt.RawJson));
                // Simple heuristic for activity state from text output
                var activityState = MapTextToState(evt.RawJson);
                if (activityState != AgentActivityState.Idle)
                {
                    Context.System.ActorSelection("/user/viewport")
                        .Tell(new AgentStateChanged(_agentId, activityState));
                }
                // Emit AG-UI events
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
                if (task.Budget?.MaxTokens is { } maxTokens)
                    _rpcActor?.Tell(new SetTokenBudget(task.TaskId, maxTokens));
                // Pre-task knowledge retrieval: query relevant context before forwarding to tasks
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
                                return (object)new RetrievalFailed(task, t.Exception?.GetBaseException().Message ?? "timeout");
                            return new RetrievalComplete(task, t.Result.Entries);
                        })
                        .PipeTo(Self);
                }
                else
                {
                    // No description — skip retrieval, forward immediately
                    Context.Child("tasks").Tell(task);
                }
                break;

            case RetrievalComplete retrieval:
                if (retrieval.Entries.Count > 0)
                {
                    // Assemble context-enriched prompt from retrieved knowledge
                    var contextLines = retrieval.Entries
                        .Select(e => $"[{e.Category}] {e.Content}")
                        .ToList();
                    var contextBlock = string.Join("\n", contextLines);
                    var enrichedPrompt = $"[Retrieved context for task]\n{contextBlock}\n\n[Task]\n{retrieval.Task.Description}";
                    _rpcActor?.Tell(new SendPrompt(_agentId, enrichedPrompt));
                    _logger.LogInformation("Agent {AgentId} retrieved {Count} knowledge entries for task {TaskId}",
                        _agentId, retrieval.Entries.Count, retrieval.Task.TaskId);
                }
                Context.System.ActorSelection("/user/viewport")
                    .Tell(new AgentMemoryActivity(_agentId, false));
                Context.Child("tasks").Tell(retrieval.Task);
                break;

            case RetrievalFailed failed:
                _logger.LogWarning("Knowledge retrieval failed for agent {AgentId} task {TaskId}: {Reason}",
                    _agentId, failed.Task.TaskId, failed.Reason);
                Context.System.ActorSelection("/user/viewport")
                    .Tell(new AgentMemoryActivity(_agentId, false));
                // Graceful degradation — proceed without context
                Context.Child("tasks").Tell(failed.Task);
                break;

            case TaskCompleted completed:
                _activeTaskCount = Math.Max(0, _activeTaskCount - 1);
                Context.Child("tasks").Tell(completed);
                if (completed.GraphId != null)
                    Context.System.ActorSelection("/user/taskgraph").Tell(completed);
                // Post-task write-back: store outcome in long-term knowledge and episodic memory
                if (completed.Summary != null)
                {
                    _knowledgeSupervisor.Tell(new StoreKnowledge(
                        _agentId, completed.Summary, "outcome",
                        new Dictionary<string, string> { ["taskId"] = completed.TaskId }));
                    _memorySupervisor.Tell(new StoreMemory(
                        _agentId, completed.TaskId, completed.Summary,
                        $"Task {completed.TaskId} completed"));
                    _logger.LogDebug("Agent {AgentId} stored outcome for task {TaskId}", _agentId, completed.TaskId);
                }
                break;

            case TaskFailed failed:
                _activeTaskCount = Math.Max(0, _activeTaskCount - 1);
                Context.Child("tasks").Tell(failed);
                if (failed.GraphId != null)
                    Context.System.ActorSelection("/user/taskgraph").Tell(failed);
                // Store failure as pitfall knowledge
                if (failed.Reason != null)
                {
                    _knowledgeSupervisor.Tell(new StoreKnowledge(
                        _agentId, failed.Reason, "pitfall",
                        new Dictionary<string, string> { ["taskId"] = failed.TaskId }));
                }
                break;

            case TaskBidRejected:
                // No action needed — bid lost
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
                    // Visual state cycling only — no fake console output
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
        // Don't bid if at capacity
        if (_activeTaskCount >= MaxConcurrentTasks) return;

        // Compute fitness: fraction of required capabilities we have
        if (available.RequiredCapabilities.Count == 0) return;
        var matchCount = available.RequiredCapabilities.Count(c => _capabilities.Contains(c));
        var fitness = (double)matchCount / available.RequiredCapabilities.Count;

        // Don't bid if fitness is below threshold
        if (fitness < MinBidThreshold) return;

        var bid = new TaskBid(
            available.TaskId,
            _agentId,
            fitness,
            _activeTaskCount,
            TimeSpan.FromMinutes(5)); // default estimate

        // Send bid directly to dispatch actor
        Context.System.ActorSelection("/user/dispatch").Tell(bid);
    }

    private static AgentActivityState MapTextToState(string text)
    {
        // Heuristic: detect activity from pi's text-mode output patterns
        if (text.Contains("tool_use") || text.Contains("Writing") || text.Contains("Creating"))
            return AgentActivityState.Typing;
        if (text.Contains("Reading") || text.Contains("read_file") || text.Contains("list_dir"))
            return AgentActivityState.Reading;
        if (text.Contains("Thinking") || text.Contains("thinking"))
            return AgentActivityState.Thinking;
        return AgentActivityState.Idle;
    }

    // Internal messages for pre-task retrieval PipeTo bridging
    private sealed record RetrievalComplete(TaskAssigned Task, IReadOnlyList<KnowledgeEntry> Entries);
    private sealed record RetrievalFailed(TaskAssigned Task, string Reason);
    private sealed record DemoTick(int Index);
}
