using Akka.Actor;
using GiantIsopod.Contracts.Core;
using GiantIsopod.Plugin.Process;
using Microsoft.Extensions.Logging;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// /user/memory/{agent} — owns a single agent's Memvid .mv2 file.
/// Delegates to Plugin.Process.MemvidClient for actual CLI operations.
/// </summary>
public sealed class MemvidActor : UntypedActor
{
    private readonly string _agentId;
    private readonly MemvidClient _client;
    private readonly ILogger<MemvidActor> _logger;

    private static readonly TimeSpan CommitDebounce = TimeSpan.FromSeconds(5);
    private ICancelable? _commitTimer;
    private bool _pendingCommit;

    public MemvidActor(string agentId, string mv2Path, string memvidExecutable, ILogger<MemvidActor> logger)
    {
        _agentId = agentId;
        _client = new MemvidClient(agentId, mv2Path, memvidExecutable);
        _logger = logger;
    }

    protected override void PostStop()
    {
        _commitTimer?.Cancel();
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

            case CommitMemory:
                HandleCommit();
                break;

            case StoreCompleted completed:
                _logger.LogDebug("Stored memory for {AgentId}: {Title}", _agentId, completed.Title);
                ScheduleDebouncedCommit();
                break;

            case SearchCompleted completed:
                completed.ReplyTo.Tell(new MemorySearchResult(
                    completed.AgentId, completed.TaskRunId, completed.Hits));
                break;

            case CommitCompleted:
                _pendingCommit = false;
                _logger.LogDebug("Memory committed for {AgentId}", _agentId);
                break;

            case MemvidOperationFailed failed:
                _logger.LogWarning("Memory operation failed for {AgentId}: {Reason}", failed.AgentId, failed.Reason);
                if (failed.ReplyTo != null)
                {
                    failed.ReplyTo.Tell(new MemorySearchResult(
                        failed.AgentId, failed.TaskRunId, []));
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
                    return (object)new MemvidOperationFailed(store.AgentId, null, t.Exception?.GetBaseException().Message ?? "unknown error", null);
                if (t.IsCanceled)
                    return (object)new MemvidOperationFailed(store.AgentId, null, "operation canceled", null);
                return new StoreCompleted(store.Title ?? "untitled");
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
                    return (object)new MemvidOperationFailed(search.AgentId, search.TaskRunId, t.Exception?.GetBaseException().Message ?? "unknown error", replyTo);
                if (t.IsCanceled)
                    return (object)new MemvidOperationFailed(search.AgentId, search.TaskRunId, "operation canceled", replyTo);
                return new SearchCompleted(search.AgentId, search.TaskRunId, t.Result, replyTo);
            })
            .PipeTo(Self);
    }

    private void HandleCommit()
    {
        if (_pendingCommit) return; // already committing
        _pendingCommit = true;

        _client.CommitAsync()
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    return (object)new MemvidOperationFailed(_agentId, null, $"commit failed: {t.Exception?.GetBaseException().Message}", null);
                if (t.IsCanceled)
                    return (object)new MemvidOperationFailed(_agentId, null, "commit canceled", null);
                return new CommitCompleted();
            })
            .PipeTo(Self);
    }

    private void ScheduleDebouncedCommit()
    {
        _commitTimer?.Cancel();
        _commitTimer = Context.System.Scheduler.ScheduleTellOnceCancelable(
            CommitDebounce, Self, new CommitMemory(_agentId), Self);
    }

    // Internal messages for PipeTo async bridging
    private sealed record StoreCompleted(string Title);
    private sealed record SearchCompleted(string AgentId, string? TaskRunId, IReadOnlyList<MemoryHit> Hits, IActorRef ReplyTo);
    private sealed record CommitCompleted;
    private sealed record MemvidOperationFailed(string AgentId, string? TaskRunId, string Reason, IActorRef? ReplyTo);
}
