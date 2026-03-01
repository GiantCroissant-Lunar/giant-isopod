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

    public MemvidActor(string agentId, string mv2Path, string memvidExecutable, ILogger<MemvidActor> logger)
    {
        _agentId = agentId;
        _client = new MemvidClient(agentId, mv2Path, memvidExecutable);
        _logger = logger;
    }

    protected override void OnReceive(object message)
    {
        switch (message)
        {
            case StoreMemory store:
                HandleStoreMemory(store);
                break;

            case SearchMemory search:
                HandleSearchMemory(search);
                break;
        }
    }

    private void HandleStoreMemory(StoreMemory store)
    {
        var self = Self;
        var sender = Sender;

        _client.PutAsync(store.Content, store.Title, store.Tags).ContinueWith(t =>
        {
            if (t.IsFaulted)
                _logger.LogWarning(t.Exception, "Failed to store memory for {AgentId}", _agentId);
            else
                _logger.LogDebug("Stored memory for {AgentId}: {Title}", _agentId, store.Title);
        });
    }

    private void HandleSearchMemory(SearchMemory search)
    {
        var sender = Sender;
        var self = Self;

        _client.SearchAsync(search.Query, search.TopK).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _logger.LogWarning(t.Exception, "Failed to search memory for {AgentId}", _agentId);
                sender.Tell(new MemorySearchResult(search.AgentId, search.TaskRunId, []), self);
            }
            else
            {
                sender.Tell(new MemorySearchResult(search.AgentId, search.TaskRunId, t.Result), self);
            }
        });
    }
}
