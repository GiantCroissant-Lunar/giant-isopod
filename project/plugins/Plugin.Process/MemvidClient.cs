using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;
using GiantIsopod.Contracts.Core;
using GiantIsopod.Contracts.Protocol.Memvid;

namespace GiantIsopod.Plugin.Process;

/// <summary>
/// Sidecar-backed wrapper for episodic .mv2 memory operations.
/// </summary>
public sealed class MemvidClient : IMemoryStore
{
    private readonly string _sidecarExecutable;

    public string AgentId { get; }
    public string FilePath { get; }

    public MemvidClient(string agentId, string filePath, string sidecarExecutable = "memory-sidecar")
    {
        AgentId = agentId;
        FilePath = filePath;
        _sidecarExecutable = CliExecutableResolver.Resolve(
            sidecarExecutable,
            CliExecutableResolver.SidecarRepoLocalCandidates(sidecarExecutable));
    }

    public async Task PutAsync(
        string content,
        string? title = null,
        IDictionary<string, string>? tags = null,
        CancellationToken ct = default)
    {
        var args = new List<string> { "episodic-put", content, "--file", FilePath };
        if (title != null)
        {
            args.Add("--title");
            args.Add(title);
        }

        if (tags != null)
        {
            foreach (var (key, value) in tags)
            {
                args.Add("--tag");
                args.Add($"{key}:{value}");
            }
        }

        await ExecuteCheckedBufferedAsync(args, ct);
    }

    public async Task<IReadOnlyList<MemoryHit>> SearchAsync(
        string query,
        int topK = 10,
        CancellationToken ct = default)
    {
        var result = await ExecuteCheckedBufferedAsync(
            ["episodic-search", query, "--file", FilePath, "--top-k", topK.ToString()],
            ct);

        if (string.IsNullOrWhiteSpace(result.StandardOutput))
            return [];

        var response = JsonSerializer.Deserialize<MemvidSearchResponse>(result.StandardOutput);
        return response?.Hits.Select(h => new MemoryHit(
            h.Text,
            h.Title,
            h.Score,
            h.Timestamp != null ? DateTimeOffset.Parse(h.Timestamp) : null)).ToList() ?? [];
    }

    public async Task CommitAsync(CancellationToken ct = default)
    {
        await ExecuteCheckedBufferedAsync(["episodic-commit", "--file", FilePath], ct);
    }

    private async Task<BufferedCommandResult> ExecuteCheckedBufferedAsync(
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var result = await Cli.Wrap(_sidecarExecutable)
            .WithArguments(args)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (!result.IsSuccess)
        {
            var error = string.IsNullOrWhiteSpace(result.StandardError)
                ? $"memory-sidecar exited with code {result.ExitCode} for args: {string.Join(" ", args)}"
                : result.StandardError.Trim();
            throw new InvalidOperationException(error);
        }

        return result;
    }
}
