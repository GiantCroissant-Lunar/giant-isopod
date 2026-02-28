using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;
using GiantIsopod.Contracts.Core;
using GiantIsopod.Contracts.Protocol.Memvid;

namespace GiantIsopod.Plugin.Process;

/// <summary>
/// CliWrap-based memvid-cli process wrapper for .mv2 file operations.
/// </summary>
public sealed class MemvidClient : IMemoryStore
{
    private readonly string _memvidExecutable;

    public string AgentId { get; }
    public string FilePath { get; }

    public MemvidClient(string agentId, string filePath, string memvidExecutable = "memvid")
    {
        AgentId = agentId;
        FilePath = filePath;
        _memvidExecutable = memvidExecutable;
    }

    public async Task PutAsync(string content, string? title = null,
        IDictionary<string, string>? tags = null, CancellationToken ct = default)
    {
        var args = new List<string> { "put", "--file", FilePath };
        if (title != null) { args.Add("--title"); args.Add(title); }
        if (tags != null)
        {
            foreach (var (key, value) in tags)
            {
                args.Add("--tag"); args.Add($"{key}:{value}");
            }
        }

        await (content | Cli.Wrap(_memvidExecutable)
            .WithArguments(args)
            .WithValidation(CommandResultValidation.None))
            .ExecuteAsync(ct);
    }

    public async Task<IReadOnlyList<MemoryHit>> SearchAsync(string query, int topK = 10,
        CancellationToken ct = default)
    {
        var result = await Cli.Wrap(_memvidExecutable)
            .WithArguments(["search", "--file", FilePath, query, "--json", "-n", topK.ToString()])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.StandardOutput))
            return [];

        var response = JsonSerializer.Deserialize<MemvidSearchResponse>(result.StandardOutput);
        return response?.Hits.Select(h => new MemoryHit(
            h.Text, h.Title, h.Score,
            h.Timestamp != null ? DateTimeOffset.Parse(h.Timestamp) : null
        )).ToList() ?? [];
    }

    public async Task CommitAsync(CancellationToken ct = default)
    {
        await Cli.Wrap(_memvidExecutable)
            .WithArguments(["commit", "--file", FilePath])
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(ct);
    }
}
