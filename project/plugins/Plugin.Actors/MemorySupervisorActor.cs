using Akka.Actor;
using GiantIsopod.Contracts.Core;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// /user/memory — supervises per-agent Memvid memory actors.
/// </summary>
public sealed class MemorySupervisorActor : UntypedActor
{
    private readonly string _memoryBasePath;
    private readonly Dictionary<string, IActorRef> _memoryActors = new();

    public MemorySupervisorActor(string memoryBasePath)
    {
        _memoryBasePath = memoryBasePath;
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
        }
    }

    private IActorRef GetOrCreateMemoryActor(string agentId)
    {
        if (!_memoryActors.TryGetValue(agentId, out var actor))
        {
            var mv2Path = Path.Combine(_memoryBasePath, $"{agentId}.mv2");
            actor = Context.ActorOf(
                Props.Create(() => new MemvidActor(agentId, mv2Path)),
                agentId);
            _memoryActors[agentId] = actor;
        }
        return actor;
    }
}
