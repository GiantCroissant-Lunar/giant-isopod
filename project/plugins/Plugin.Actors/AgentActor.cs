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
    private readonly IActorRef _registry;
    private readonly IActorRef _memorySupervisor;
    private readonly AgentWorldConfig _config;
    private readonly ILogger<AgentActor> _logger;

    private IActorRef? _rpcActor;
    private bool _piConnected;
    private ICancelable? _demoTimer;

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
        ILogger<AgentActor> logger)
    {
        _agentId = agentId;
        _aieosProfilePath = aieosProfilePath;
        _skillBundleName = skillBundleName;
        _memoryFilePath = memoryFilePath;
        _registry = registry;
        _memorySupervisor = memorySupervisor;
        _config = config;
        _logger = logger;
    }

    protected override void PreStart()
    {
        _rpcActor = Context.ActorOf(
            Props.Create(() => new AgentRpcActor(_agentId, _config)),
            "rpc");

        Context.ActorOf(
            Props.Create(() => new AgentTaskActor(_agentId)),
            "tasks");

        // Load AIEOS profile for visual info and capabilities
        var visualInfo = LoadVisualInfo();
        var capabilities = LoadCapabilities();

        _registry.Tell(new RegisterSkills(_agentId, capabilities));

        // Notify viewport with rich visual info
        Context.System.ActorSelection("/user/viewport")
            .Tell(new AgentSpawnedWithVisuals(_agentId, visualInfo));

        if (_memoryFilePath != null)
        {
            _memorySupervisor.Tell(new StoreMemory(_agentId, $"Agent {_agentId} started", "session_start"));
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
                Context.System.ActorSelection("/user/viewport")
                    .Tell(new AgentStateChanged(_agentId, MapEventToState(evt.RawJson)));
                Context.System.ActorSelection("/user/viewport")
                    .Tell(new ProcessOutput(_agentId, evt.RawJson));
                if (_memoryFilePath != null)
                    _memorySupervisor.Tell(new StoreMemory(_agentId, evt.RawJson, "process_event"));
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

            case TaskAssigned task:
                Context.Child("tasks").Forward(task);
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
                // _aieosProfilePath contains the JSON content (read by Godot from PCK)
                var entity = System.Text.Json.JsonSerializer.Deserialize<
                    GiantIsopod.Contracts.Protocol.Aieos.AieosEntity>(_aieosProfilePath);

                if (entity != null)
                {
                    var displayName = entity.Identity?.Names?.First
                        ?? entity.Metadata?.Alias
                        ?? _agentId;

                    return new AgentVisualInfo(
                        _agentId,
                        displayName,
                        SkinTone: entity.Physicality?.Face?.Skin?.Tone,
                        HairStyle: entity.Physicality?.Face?.Hair?.Style,
                        HairColor: entity.Physicality?.Face?.Hair?.Color,
                        AestheticArchetype: entity.Physicality?.Style?.AestheticArchetype);
                }
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

                if (entity?.Capabilities?.Skills != null)
                {
                    return entity.Capabilities.Skills
                        .Where(s => s.Name != null)
                        .Select(s => s.Name!)
                        .ToHashSet();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load capabilities for {AgentId}", _agentId);
        }

        return new HashSet<string>();
    }

    private static AgentActivityState MapEventToState(string rawJson)
    {
        if (rawJson.Contains("\"tool_use\"")) return AgentActivityState.Typing;
        if (rawJson.Contains("\"tool_result\"")) return AgentActivityState.Reading;
        if (rawJson.Contains("\"thinking\"")) return AgentActivityState.Thinking;
        return AgentActivityState.Idle;
    }

    private sealed record DemoTick(int Index);
}
