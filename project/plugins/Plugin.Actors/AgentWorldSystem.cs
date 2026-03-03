using Akka.Actor;
using Akka.Configuration;
using GiantIsopod.Plugin.Process;
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
    public IActorRef KnowledgeSupervisor { get; }
    public IActorRef Blackboard { get; }
    public IActorRef AgentSupervisor { get; }
    public IActorRef Dispatch { get; }
    public IActorRef TaskGraph { get; }
    public IActorRef Viewport { get; }
    public IActorRef Artifacts { get; }
    public IActorRef ArtifactFollowups { get; }
    public IActorRef Workspace { get; }
    public IActorRef Validator { get; }
    public IActorRef A2A { get; }

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
                config.MemorySidecarExecutable,
                loggerFactory)),
            "memory");

        var sidecarClient = new MemorySidecarClient(
            config.MemoryBasePath,
            config.MemorySidecarExecutable);

        KnowledgeSupervisor = _system.ActorOf(
            Props.Create(() => new KnowledgeSupervisorActor(
                sidecarClient,
                loggerFactory)),
            "knowledge");

        Blackboard = _system.ActorOf(
            Props.Create(() => new BlackboardActor(
                loggerFactory.CreateLogger<BlackboardActor>())),
            "blackboard");

        Artifacts = _system.ActorOf(
            Props.Create(() => new ArtifactRegistryActor(
                loggerFactory.CreateLogger<ArtifactRegistryActor>())),
            "artifacts");

        AgentSupervisor = _system.ActorOf(
            Props.Create(() => new AgentSupervisorActor(
                Registry, Artifacts, MemorySupervisor, KnowledgeSupervisor, config,
                loggerFactory)),
            "agents");

        // Workspace must be created before Dispatch and TaskGraph (they depend on it)
        var anchorRepoPath = !string.IsNullOrEmpty(config.AnchorRepoPath)
            ? config.AnchorRepoPath
            : config.RuntimeWorkingDirectory;

        Workspace = _system.ActorOf(
            Props.Create(() => new WorkspaceActor(
                anchorRepoPath, config.IntegrationBranch,
                loggerFactory.CreateLogger<WorkspaceActor>())),
            "workspace");

        Dispatch = _system.ActorOf(
            Props.Create(() => new DispatchActor(
                Registry, AgentSupervisor, Workspace,
                loggerFactory.CreateLogger<DispatchActor>())),
            "dispatch");

        // Viewport must be created before TaskGraph so it can receive notifications
        Viewport = _system.ActorOf(
            Props.Create(() => new ViewportActor(
                loggerFactory.CreateLogger<ViewportActor>())),
            "viewport");

        Validator = _system.ActorOf(
            Props.Create(() => new ValidatorActor(
                Artifacts,
                config,
                loggerFactory.CreateLogger<ValidatorActor>())),
            "validator");

        TaskGraph = _system.ActorOf(
            Props.Create(() => new TaskGraphActor(
                Dispatch, AgentSupervisor, Viewport, Workspace, Validator, KnowledgeSupervisor,
                CreateCheckpointStore(config),
                loggerFactory.CreateLogger<TaskGraphActor>())),
            "taskgraph");

        ArtifactFollowups = _system.ActorOf(
            Props.Create(() => new ArtifactFollowupActor(
                Artifacts,
                TaskGraph,
                loggerFactory.CreateLogger<ArtifactFollowupActor>())),
            "artifact-followups");

        A2A = _system.ActorOf(
            Props.Create(() => new A2AActor(
                Dispatch, Registry,
                loggerFactory.CreateLogger<A2AActor>())),
            "a2a");

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

    private static ITaskGraphCheckpointStore CreateCheckpointStore(AgentWorldConfig config)
    {
        var checkpointBasePath = !string.IsNullOrWhiteSpace(config.CheckpointBasePath)
            ? config.CheckpointBasePath
            : Path.Combine(config.MemoryBasePath, "task-graph-checkpoints");

        return new FileTaskGraphCheckpointStore(checkpointBasePath);
    }
}

public record AgentWorldConfig
{
    public required string SkillsBasePath { get; init; }
    public required string MemoryBasePath { get; init; }
    public required string AgentDataPath { get; init; }

    /// <summary>Runtime registry loaded from runtimes.json (or legacy cli-providers.json).</summary>
    public required GiantIsopod.Plugin.Process.RuntimeRegistry Runtimes { get; init; }

    /// <summary>Default runtime id when spawning agents (null = first in list).</summary>
    public string? DefaultRuntimeId { get; init; }

    /// <summary>Working directory for agent runtimes.</summary>
    public string RuntimeWorkingDirectory { get; init; } = "";

    /// <summary>Anchor repo path for workspace worktrees. Defaults to RuntimeWorkingDirectory.</summary>
    public string AnchorRepoPath { get; init; } = "";

    /// <summary>Branch that task worktrees should merge back into.</summary>
    public string IntegrationBranch { get; init; } = "main";

    /// <summary>Extra environment variables merged into all agent runtimes (e.g. API keys).</summary>
    public Dictionary<string, string> RuntimeEnvironment { get; init; } = new();

    public string MemvidExecutable { get; init; } = "memvid";
    public string MemorySidecarExecutable { get; init; } = "memory-sidecar";
    public string CheckpointBasePath { get; init; } = "";
}
