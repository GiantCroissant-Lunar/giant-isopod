using Akka.Actor;
using GiantIsopod.Contracts.Core;
using GiantIsopod.Plugin.Process;
using Microsoft.Extensions.Logging;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// /user/knowledge/{agent} — owns a single agent's long-term knowledge store.
/// Delegates to Plugin.Process.MemorySidecarClient for actual CLI operations.
/// </summary>
public sealed class KnowledgeStoreActor : UntypedActor
{
    private readonly string _agentId;
    private readonly MemorySidecarClient _client;
    private readonly ILogger<KnowledgeStoreActor> _logger;

    public KnowledgeStoreActor(string agentId, MemorySidecarClient client, ILogger<KnowledgeStoreActor> logger)
    {
        _agentId = agentId;
        _client = client;
        _logger = logger;
    }

    protected override void OnReceive(object message)
    {
        switch (message)
        {
            case StoreKnowledge store:
                HandleStore(store);
                break;

            case QueryKnowledge query:
                HandleQuery(query);
                break;
        }
    }

    private void HandleStore(StoreKnowledge store)
    {
        var sender = Sender;
        var self = Self;

        _client.StoreKnowledgeAsync(store.AgentId, store.Content, store.Category, store.Tags)
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    _logger.LogWarning(t.Exception, "Failed to store knowledge for {AgentId}", _agentId);
                else
                    _logger.LogDebug("Stored knowledge for {AgentId}: category={Category}, id={Id}",
                        _agentId, store.Category, t.Result);
            });
    }

    private void HandleQuery(QueryKnowledge query)
    {
        var sender = Sender;
        var self = Self;

        _client.SearchKnowledgeAsync(query.AgentId, query.Query, query.Category, query.TopK)
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger.LogWarning(t.Exception, "Failed to query knowledge for {AgentId}", _agentId);
                    sender.Tell(new KnowledgeResult(query.AgentId, []), self);
                }
                else
                {
                    var entries = t.Result.Select(r => new KnowledgeEntry(
                        r.Content, r.Category, r.Relevance, r.Tags,
                        DateTimeOffset.TryParse(r.StoredAt, out var dt) ? dt : DateTimeOffset.MinValue
                    )).ToList();
                    sender.Tell(new KnowledgeResult(query.AgentId, entries), self);
                }
            });
    }
}
