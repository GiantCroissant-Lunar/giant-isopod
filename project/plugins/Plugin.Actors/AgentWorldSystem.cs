using Akka.Actor;
using Akka.Configuration;
using Microsoft.Extensions.Logging;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// Bootstraps the Akka.NET actor system and creates the top-level actor tree.
/// </summary>
public sealed class AgentWorldSystem : IDisposable
{
    private readonly ActorSystem _system;
    private readonly ILogger<AgentWorldSystem> _logger;

    public IActorRef Registry { get; }
    public IActorRef MemorySupervisor { get; }
    public IActorRef Blackboard { get; }
    public IActorRef AgentSupervisor { get; }
    public IActorRef Dispatch { get; }
    public IActorRef TaskGraph { get; }
    public IActorRef Viewport { get; }

    public AgentWorldSystem(AgentWorldConfig config, ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<AgentWorldSystem>();

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
            Props.Create(() => new SkillRegistryActor(
                loggerFactory.CreateLogger<SkillRegistryActor>())),
            "registry");

        MemorySupervisor = _system.ActorOf(
            Props.Create(() => new MemorySupervisorActor(
                config.MemoryBasePath,
                loggerFactory.CreateLogger<MemorySupervisorActor>())),
            "memory");

        Blackboard = _system.ActorOf(
            Props.Create(() => new BlackboardActor(
                loggerFactory.CreateLogger<BlackboardActor>())),
            "blackboard");

        AgentSupervisor = _system.ActorOf(
            Props.Create(() => new AgentSupervisorActor(
                Registry, MemorySupervisor, config,
                loggerFactory)),
            "agents");

        Dispatch = _system.ActorOf(
            Props.Create(() => new DispatchActor(
                Registry, AgentSupervisor,
                loggerFactory.CreateLogger<DispatchActor>())),
            "dispatch");

        TaskGraph = _system.ActorOf(
            Props.Create(() => new TaskGraphActor(
                Dispatch,
                loggerFactory.CreateLogger<TaskGraphActor>())),
            "taskgraph");

        Viewport = _system.ActorOf(
            Props.Create(() => new ViewportActor(
                loggerFactory.CreateLogger<ViewportActor>())),
            "viewport");

        _logger.LogInformation("Actor system started");
    }

    public void SetViewportBridge(GiantIsopod.Contracts.Core.IViewportBridge bridge)
    {
        Viewport.Tell(new GiantIsopod.Contracts.Core.SetViewportBridge(bridge));
    }

    public void Dispose()
    {
        _system.Terminate().Wait(TimeSpan.FromSeconds(10));
        _system.Dispose();
        _logger.LogInformation("Actor system stopped");
    }
}

public record AgentWorldConfig
{
    public required string SkillsBasePath { get; init; }
    public required string MemoryBasePath { get; init; }
    public required string AgentDataPath { get; init; }

    /// <summary>CLI provider registry loaded from cli-providers.json.</summary>
    public required GiantIsopod.Plugin.Process.CliProviderRegistry CliProviders { get; init; }

    /// <summary>Default CLI provider id when spawning agents (null = first in list).</summary>
    public string? DefaultCliProviderId { get; init; }

    /// <summary>Working directory for CLI processes.</summary>
    public string CliWorkingDirectory { get; init; } = "";

    /// <summary>Extra environment variables merged into all CLI processes (e.g. API keys).</summary>
    public Dictionary<string, string> CliEnvironment { get; init; } = new();

    public string MemvidExecutable { get; init; } = "memvid";
}
