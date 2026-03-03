using Akka.Actor;
using GiantIsopod.Contracts.Core;
using Microsoft.Extensions.Logging;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// /user/memory — supervises per-agent Memvid memory actors.
/// </summary>
public sealed class MemorySupervisorActor : UntypedActor
{
    private readonly string _memoryBasePath;
    private readonly string _memorySidecarExecutable;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MemorySupervisorActor> _logger;
    private readonly Dictionary<string, IActorRef> _memoryActors = new();

    public MemorySupervisorActor(string memoryBasePath, string memorySidecarExecutable, ILoggerFactory loggerFactory)
    {
        _memoryBasePath = memoryBasePath;
        _memorySidecarExecutable = memorySidecarExecutable;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<MemorySupervisorActor>();
    }

    protected override void OnReceive(object message)
    {
        switch (message)
        {
            case StoreMemory store:
                GetOrCreateMemoryActor(store.AgentId).Forward(store);
                break;

            case SearchMemory search:
                GetOrCreateMemoryActor(search.AgentId).Forward(search);
                break;

            case CommitMemory commit:
                GetOrCreateMemoryActor(commit.AgentId).Forward(commit);
                break;
        }
    }

    private IActorRef GetOrCreateMemoryActor(string agentId)
    {
        if (!_memoryActors.TryGetValue(agentId, out var actor))
        {
            var mv2Path = Path.Combine(_memoryBasePath, $"{agentId}.mv2");
            actor = Context.ActorOf(
                Props.Create(() => new MemvidActor(
                    agentId, mv2Path, _memorySidecarExecutable,
                    _loggerFactory.CreateLogger<MemvidActor>())),
                agentId);
            _memoryActors[agentId] = actor;
            _logger.LogDebug("Created memory actor for {AgentId} at {Path}", agentId, mv2Path);
        }
        return actor;
    }
}
