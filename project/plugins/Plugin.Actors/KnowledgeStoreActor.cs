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

            case StoreCompleted completed:
                _logger.LogDebug("Stored knowledge for {AgentId}: category={Category}", _agentId, completed.Category);
                break;

            case StoreFailed failed:
                _logger.LogWarning("Failed to store knowledge for {AgentId}: {Reason}", _agentId, failed.Reason);
                break;

            case QueryCompleted completed:
                completed.ReplyTo.Tell(new KnowledgeResult(completed.AgentId, completed.Entries), Self);
                break;

            case QueryFailed failed:
                _logger.LogWarning("Failed to query knowledge for {AgentId}: {Reason}", _agentId, failed.Reason);
                failed.ReplyTo.Tell(new KnowledgeResult(failed.AgentId, []), Self);
                break;
        }
    }

    private void HandleStore(StoreKnowledge store)
    {
        _client.StoreKnowledgeAsync(store.AgentId, store.Content, store.Category, store.Tags)
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    return (object)new StoreFailed(t.Exception?.GetBaseException().Message ?? "unknown error");
                if (t.IsCanceled)
                    return (object)new StoreFailed("operation canceled");
                return new StoreCompleted(store.Category);
            })
            .PipeTo(Self);
    }

    private void HandleQuery(QueryKnowledge query)
    {
        var replyTo = Sender;
        _client.SearchKnowledgeAsync(query.AgentId, query.Query, query.Category, query.TopK)
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    return (object)new QueryFailed(query.AgentId, t.Exception?.GetBaseException().Message ?? "unknown error", replyTo);
                if (t.IsCanceled)
                    return (object)new QueryFailed(query.AgentId, "operation canceled", replyTo);

                var entries = t.Result.Select(r => new KnowledgeEntry(
                    r.Content, r.Category, r.Relevance, r.Tags,
                    DateTimeOffset.TryParse(r.StoredAt, out var dt) ? dt : DateTimeOffset.MinValue
                )).ToList();
                return new QueryCompleted(query.AgentId, entries, replyTo);
            })
            .PipeTo(Self);
    }

    // Internal messages for PipeTo async bridging
    private sealed record StoreCompleted(string Category);
    private sealed record StoreFailed(string Reason);
    private sealed record QueryCompleted(string AgentId, IReadOnlyList<KnowledgeEntry> Entries, IActorRef ReplyTo);
    private sealed record QueryFailed(string AgentId, string Reason, IActorRef ReplyTo);
}
