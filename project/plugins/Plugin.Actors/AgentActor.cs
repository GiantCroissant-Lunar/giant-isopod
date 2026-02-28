using Akka.Actor;
using GiantIsopod.Contracts.Core;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// /user/agents/{name} — owns an agent's pi process, skill registration, and memory reference.
/// Child actors: rpc (process pipe), tasks (task lifecycle).
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

    private IActorRef? _rpcActor;

    public AgentActor(
        string agentId,
        string aieosProfilePath,
        string skillBundleName,
        string? memoryFilePath,
        IActorRef registry,
        IActorRef memorySupervisor,
        AgentWorldConfig config)
    {
        _agentId = agentId;
        _aieosProfilePath = aieosProfilePath;
        _skillBundleName = skillBundleName;
        _memoryFilePath = memoryFilePath;
        _registry = registry;
        _memorySupervisor = memorySupervisor;
        _config = config;
    }

    protected override void PreStart()
    {
        // Create child actors
        _rpcActor = Context.ActorOf(
            Props.Create(() => new AgentRpcActor(_agentId, _config.PiExecutable)),
            "rpc");

        Context.ActorOf(
            Props.Create(() => new AgentTaskActor(_agentId)),
            "tasks");

        // Register capabilities with the skill registry
        // TODO: Load skill bundle, derive capabilities, register
        _registry.Tell(new RegisterSkills(_agentId, new HashSet<string>()));

        // Initialize memory
        if (_memoryFilePath != null)
        {
            _memorySupervisor.Tell(new StoreMemory(_agentId, $"Agent {_agentId} started", "session_start"));
        }

        // Start the pi process
        _rpcActor.Tell(new StartProcess(_agentId));
    }

    protected override void PostStop()
    {
        _registry.Tell(new UnregisterSkills(_agentId));
    }

    protected override void OnReceive(object message)
    {
        switch (message)
        {
            case SendPrompt prompt:
                _rpcActor?.Forward(prompt);
                break;

            case ProcessEvent evt:
                // Forward to viewport for rendering
                Context.System.ActorSelection("/user/viewport")
                    .Tell(new AgentStateChanged(_agentId, MapEventToState(evt.RawJson)));

                // Store in memory
                if (_memoryFilePath != null)
                {
                    _memorySupervisor.Tell(new StoreMemory(_agentId, evt.RawJson, "process_event"));
                }
                break;

            case ProcessExited exited:
                Context.System.ActorSelection("/user/viewport")
                    .Tell(new AgentStateChanged(_agentId, AgentActivityState.Idle));
                break;

            case TaskAssigned task:
                Context.Child("tasks").Forward(task);
                break;
        }
    }

    private static AgentActivityState MapEventToState(string rawJson)
    {
        // TODO: Parse pi RPC event and map tool_use types to activity states
        if (rawJson.Contains("\"tool_use\"")) return AgentActivityState.Typing;
        if (rawJson.Contains("\"tool_result\"")) return AgentActivityState.Reading;
        if (rawJson.Contains("\"thinking\"")) return AgentActivityState.Thinking;
        return AgentActivityState.Idle;
    }
}
