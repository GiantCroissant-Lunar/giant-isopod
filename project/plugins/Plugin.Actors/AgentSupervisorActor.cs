using Akka.Actor;
using GiantIsopod.Contracts.Core;
using Microsoft.Extensions.Logging;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// /user/agents — supervises all agent actors.
/// Restart strategy: restart individual agent on failure, up to 3 times per minute.
/// </summary>
public sealed class AgentSupervisorActor : UntypedActor
{
    private readonly IActorRef _registry;
    private readonly IActorRef _memorySupervisor;
    private readonly AgentWorldConfig _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AgentSupervisorActor> _logger;
    private readonly Dictionary<string, IActorRef> _agents = new();

    public AgentSupervisorActor(
        IActorRef registry,
        IActorRef memorySupervisor,
        AgentWorldConfig config,
        ILoggerFactory loggerFactory)
    {
        _registry = registry;
        _memorySupervisor = memorySupervisor;
        _config = config;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<AgentSupervisorActor>();
    }

    protected override SupervisorStrategy SupervisorStrategy()
    {
        return new OneForOneStrategy(
            maxNrOfRetries: 3,
            withinTimeRange: TimeSpan.FromMinutes(1),
            decider: Decider.From(ex => ex switch
            {
                _ => Directive.Restart
            }));
    }

    protected override void OnReceive(object message)
    {
        switch (message)
        {
            case SpawnAgent spawn:
                if (_agents.ContainsKey(spawn.AgentId))
                {
                    Sender.Tell(new AgentSpawned(spawn.AgentId));
                    return;
                }

                var agentRef = Context.ActorOf(
                    Props.Create(() => new AgentActor(
                        spawn.AgentId,
                        spawn.AieosProfilePath,
                        spawn.SkillBundleName,
                        spawn.MemoryFilePath,
                        _registry,
                        _memorySupervisor,
                        _config,
                        _loggerFactory.CreateLogger<AgentActor>())),
                    spawn.AgentId);

                _agents[spawn.AgentId] = agentRef;
                _logger.LogInformation("Spawned agent {AgentId}", spawn.AgentId);
                Sender.Tell(new AgentSpawned(spawn.AgentId));
                // Notify viewport so the UI can render the agent
                Context.System.ActorSelection("/user/viewport")
                    .Tell(new AgentSpawned(spawn.AgentId));
                break;

            case StopAgent stop:
                if (_agents.TryGetValue(stop.AgentId, out var actor))
                {
                    Context.Stop(actor);
                    _agents.Remove(stop.AgentId);
                    _logger.LogInformation("Stopped agent {AgentId}", stop.AgentId);
                    Sender.Tell(new AgentStopped(stop.AgentId));
                    Context.System.ActorSelection("/user/viewport")
                        .Tell(new AgentStopped(stop.AgentId));
                }
                break;

            case SendPrompt prompt:
                if (_agents.TryGetValue(prompt.AgentId, out var target))
                    target.Forward(message);
                break;

            case TaskAssigned task:
                if (_agents.TryGetValue(task.AgentId, out var taskTarget))
                    taskTarget.Forward(message);
                break;
        }
    }
}
