using Akka.Actor;
using GiantIsopod.Contracts.Core;
using GiantIsopod.Plugin.Process;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// /user/memory/{agent} — owns a single agent's Memvid .mv2 file.
/// Delegates to Plugin.Process.MemvidClient for actual CLI operations.
/// </summary>
public sealed class MemvidActor : UntypedActor
{
    private readonly string _agentId;
    private readonly MemvidClient _client;

    public MemvidActor(string agentId, string mv2Path, string memvidExecutable)
    {
        _agentId = agentId;
        _client = new MemvidClient(agentId, mv2Path, memvidExecutable);
    }

    protected override void OnReceive(object message)
    {
        switch (message)
        {
            case StoreMemory store:
                HandleStore(store);
                break;

            case SearchMemory search:
                HandleSearch(search);
                break;

            case StoreCompleted:
                // fire-and-forget confirmation; no reply needed
                break;

            case SearchCompleted completed:
                completed.ReplyTo.Tell(new MemorySearchResult(
                    completed.AgentId, completed.TaskRunId, completed.Hits));
                break;

            case MemvidOperationFailed failed:
                if (failed.ReplyTo != null)
                {
                    failed.ReplyTo.Tell(new MemorySearchResult(
                        _agentId, null, []));
                }
                break;
        }
    }

    private void HandleStore(StoreMemory store)
    {
        _client.PutAsync(store.Content, store.Title, store.Tags)
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    return (object)new MemvidOperationFailed(t.Exception?.GetBaseException().Message ?? "unknown error", null);
                return new StoreCompleted();
            })
            .PipeTo(Self);
    }

    private void HandleSearch(SearchMemory search)
    {
        var replyTo = Sender;
        _client.SearchAsync(search.Query, search.TopK)
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    return (object)new MemvidOperationFailed(t.Exception?.GetBaseException().Message ?? "unknown error", replyTo);
                return new SearchCompleted(search.AgentId, search.TaskRunId, t.Result, replyTo);
            })
            .PipeTo(Self);
    }

    // Internal messages for PipeTo async bridging
    private sealed record StoreCompleted;
    private sealed record SearchCompleted(string AgentId, string? TaskRunId, IReadOnlyList<MemoryHit> Hits, IActorRef ReplyTo);
    private sealed record MemvidOperationFailed(string Reason, IActorRef? ReplyTo);
}
