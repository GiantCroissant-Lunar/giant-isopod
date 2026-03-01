using Akka.Actor;
using GiantIsopod.Contracts.Core;
using GiantIsopod.Plugin.Process;
using Microsoft.Extensions.Logging;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// /user/knowledge — supervises per-agent KnowledgeStoreActor instances.
/// </summary>
public sealed class KnowledgeSupervisorActor : UntypedActor
{
    private readonly MemorySidecarClient _sidecarClient;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<KnowledgeSupervisorActor> _logger;
    private readonly Dictionary<string, IActorRef> _knowledgeActors = new();

    public KnowledgeSupervisorActor(
        MemorySidecarClient sidecarClient,
        ILoggerFactory loggerFactory)
    {
        _sidecarClient = sidecarClient;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<KnowledgeSupervisorActor>();
    }

    protected override void OnReceive(object message)
    {
        switch (message)
        {
            case StoreKnowledge store:
                GetOrCreateActor(store.AgentId).Forward(store);
                break;

            case QueryKnowledge query:
                GetOrCreateActor(query.AgentId).Forward(query);
                break;
        }
    }

    private IActorRef GetOrCreateActor(string agentId)
    {
        if (!_knowledgeActors.TryGetValue(agentId, out var actor))
        {
            actor = Context.ActorOf(
                Props.Create(() => new KnowledgeStoreActor(
                    agentId, _sidecarClient,
                    _loggerFactory.CreateLogger<KnowledgeStoreActor>())),
                agentId);
            _knowledgeActors[agentId] = actor;
            _logger.LogDebug("Created knowledge actor for {AgentId}", agentId);
        }
        return actor;
    }
}
