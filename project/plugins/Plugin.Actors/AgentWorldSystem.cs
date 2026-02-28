using Akka.Actor;
using Akka.Configuration;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// Bootstraps the Akka.NET actor system and creates the top-level actor tree.
/// </summary>
public sealed class AgentWorldSystem : IDisposable
{
    private readonly ActorSystem _system;

    public IActorRef Registry { get; }
    public IActorRef MemorySupervisor { get; }
    public IActorRef AgentSupervisor { get; }
    public IActorRef Dispatch { get; }
    public IActorRef Viewport { get; }

    public AgentWorldSystem(AgentWorldConfig config)
    {
        var hocon = ConfigurationFactory.ParseString(@"
            akka {
                loglevel = INFO
                actor {
                    default-dispatcher {
                        type = TaskDispatcher
                    }
                }
            }
        ");

        _system = ActorSystem.Create("agent-world", hocon);

        Registry = _system.ActorOf(
            Props.Create(() => new SkillRegistryActor()),
            "registry");

        MemorySupervisor = _system.ActorOf(
            Props.Create(() => new MemorySupervisorActor(config.MemoryBasePath)),
            "memory");

        AgentSupervisor = _system.ActorOf(
            Props.Create(() => new AgentSupervisorActor(Registry, MemorySupervisor, config)),
            "agents");

        Dispatch = _system.ActorOf(
            Props.Create(() => new DispatchActor(Registry, AgentSupervisor)),
            "dispatch");

        Viewport = _system.ActorOf(
            Props.Create(() => new ViewportActor()),
            "viewport");
    }

    public void Dispose()
    {
        _system.Terminate().Wait(TimeSpan.FromSeconds(10));
        _system.Dispose();
    }
}

public record AgentWorldConfig
{
    public required string SkillsBasePath { get; init; }
    public required string MemoryBasePath { get; init; }
    public required string AgentDataPath { get; init; }
    public string PiExecutable { get; init; } = "pi";
    public string MemvidExecutable { get; init; } = "memvid";
}
