using Akka.Actor;
using GiantIsopod.Contracts.Core;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// /user/memory/{agent} — owns a single agent's Memvid .mv2 file.
/// Delegates to Plugin.Process.MemvidClient for actual CLI operations.
/// </summary>
public sealed class MemvidActor : UntypedActor
{
    private readonly string _agentId;
    private readonly string _mv2Path;

    public MemvidActor(string agentId, string mv2Path)
    {
        _agentId = agentId;
        _mv2Path = mv2Path;
    }

    protected override void OnReceive(object message)
    {
        switch (message)
        {
            case StoreMemory store:
                // TODO: Call MemvidClient.PutAsync via CliWrap
                // memvid put --file {_mv2Path} --title "{store.Title}" < content
                break;

            case SearchMemory search:
                // TODO: Call MemvidClient.SearchAsync via CliWrap
                // memvid search --file {_mv2Path} "{search.Query}" --json -n {search.TopK}
                // Parse response, reply with MemorySearchResult
                Sender.Tell(new MemorySearchResult(search.AgentId, search.TaskRunId, []));
                break;
        }
    }
}
