using Akka.Actor;
using GiantIsopod.Contracts.Core;
using Microsoft.Extensions.Logging;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// /user/agents/{name} — owns an agent's pi process, skill registration, and memory reference.
/// Child actors: rpc (process pipe), tasks (task lifecycle).
/// Loads AIEOS profile for visual identity. Runs a demo activity cycle when pi is not connected.
/// </summary>
public sealed class AgentActor : UntypedActor
{
    private readonly string _agentId;
    private readonly string _aieosProfilePath;
    private readonly string _skillBundleName;
    private readonly string? _memoryFilePath;
    private readonly string? _cliProviderId;
    private readonly IActorRef _registry;
    private readonly IActorRef _memorySupervisor;
    private readonly AgentWorldConfig _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AgentActor> _logger;
    private readonly GiantIsopod.Plugin.Mapping.ProtocolMapper _mapper = new();

    private IActorRef? _rpcActor;
    private bool _piConnected;
    private ICancelable? _demoTimer;

    private HashSet<string> _capabilities = new();
    private readonly Dictionary<string, string> _workingMemory = new();
    private int _activeTaskCount;
    private const int MaxConcurrentTasks = 3;
    private const double MinBidThreshold = 0.5;

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
        AgentWorldConfig config,
        ILoggerFactory loggerFactory,
        string? cliProviderId = null)
    {
        _agentId = agentId;
        _aieosProfilePath = aieosProfilePath;
        _skillBundleName = skillBundleName;
        _memoryFilePath = memoryFilePath;
        _cliProviderId = cliProviderId;
        _registry = registry;
        _memorySupervisor = memorySupervisor;
        _config = config;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<AgentActor>();
    }

    protected override void PreStart()
    {
        _rpcActor = Context.ActorOf(
            Props.Create(() => new AgentRpcActor(_agentId, _config, _cliProviderId)),
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

        _rpcActor.Tell(new StartProcess(_agentId));
        _logger.LogInformation("Agent {AgentId} started (bundle: {Bundle})", _agentId, _skillBundleName);

        // Start demo activity cycle (replaced by real pi events when connected)
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

            case ProcessStarted started when started.ProcessId > 0:
                _piConnected = true;
                _demoTimer?.Cancel();
                Context.System.ActorSelection("/user/viewport")
                    .Tell(new ProcessStarted(_agentId, started.ProcessId));
                _logger.LogInformation("Agent {AgentId} pi connected (pid: {Pid})", _agentId, started.ProcessId);
                break;

            case ProcessEvent evt:
                // Forward raw text to terminal renderer
                Context.System.ActorSelection("/user/viewport")
                    .Tell(new ProcessOutput(_agentId, evt.RawJson));
                // Simple heuristic for activity state from text output
                var activityState = MapTextToState(evt.RawJson);
                if (activityState != AgentActivityState.Idle)
                {
                    Context.System.ActorSelection("/user/viewport")
                        .Tell(new AgentStateChanged(_agentId, activityState));
                }
                break;

            case ProcessExited exited:
                _piConnected = false;
                _logger.LogWarning("Agent {AgentId} process exited (code: {Code})", _agentId, exited.ExitCode);
                Context.System.ActorSelection("/user/viewport")
                    .Tell(new AgentStateChanged(_agentId, AgentActivityState.Idle));
                Context.System.ActorSelection("/user/viewport")
                    .Tell(new ProcessExited(_agentId, exited.ExitCode));
                StartDemoTimer();
                break;

            case TaskAvailable available:
                EvaluateAndBid(available);
                break;

            case TaskAssigned task:
                _activeTaskCount++;
                if (task.Budget?.MaxTokens is { } maxTokens)
                    _rpcActor?.Tell(new SetTokenBudget(task.TaskId, maxTokens));
                Context.Child("tasks").Forward(task);
                break;

            case TaskCompleted completed:
                _activeTaskCount = Math.Max(0, _activeTaskCount - 1);
                Context.Child("tasks").Forward(completed);
                break;

            case TaskFailed failed:
                _activeTaskCount = Math.Max(0, _activeTaskCount - 1);
                Context.Child("tasks").Forward(failed);
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
                if (!_piConnected)
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

    private sealed record DemoTick(int Index);
}
