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
    private readonly SemaphoreSlim _createLock = new(1, 1);
    private bool _memoryCreated;

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
        await EnsureCreatedAsync(ct);

        var args = new List<string> { "put", FilePath };
        if (title != null) { args.Add("--title"); args.Add(title); }
        if (tags != null)
        {
            foreach (var (key, value) in tags)
            {
                args.Add("--tag"); args.Add($"{key}={value}");
            }
        }

        await ExecuteCheckedAsync(args, content, ct);
    }

    public async Task<IReadOnlyList<MemoryHit>> SearchAsync(string query, int topK = 10,
        CancellationToken ct = default)
    {
        if (!File.Exists(FilePath))
            return [];

        var result = await ExecuteCheckedBufferedAsync(
            ["find", "--query", query, FilePath, "--json", "--top-k", topK.ToString()],
            ct);

        if (string.IsNullOrWhiteSpace(result.StandardOutput))
            return [];

        var response = JsonSerializer.Deserialize<MemvidSearchResponse>(result.StandardOutput);
        return response?.Hits.Select(h => new MemoryHit(
            h.Text, h.Title, h.Score,
            h.Timestamp != null ? DateTimeOffset.Parse(h.Timestamp) : null
        )).ToList() ?? [];
    }

    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (!File.Exists(FilePath))
            return;

        await ExecuteCheckedAsync(["verify-single-file", FilePath], null, ct);
    }

    private async Task EnsureCreatedAsync(CancellationToken ct)
    {
        if (_memoryCreated && File.Exists(FilePath))
            return;

        await _createLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            if (File.Exists(FilePath))
            {
                _memoryCreated = true;
                return;
            }

            await ExecuteCheckedAsync(["create", FilePath], null, ct);
            _memoryCreated = true;
        }
        finally
        {
            _createLock.Release();
        }
    }

    private async Task ExecuteCheckedAsync(
        IReadOnlyList<string> args,
        string? standardInput,
        CancellationToken ct)
    {
        var command = Cli.Wrap(_memvidExecutable)
            .WithArguments(args)
            .WithValidation(CommandResultValidation.None);

        CommandResult result;
        if (standardInput != null)
        {
            result = await (standardInput | command).ExecuteAsync(ct);
        }
        else
        {
            result = await command.ExecuteAsync(ct);
        }

        if (!result.IsSuccess)
            throw new InvalidOperationException($"memvid exited with code {result.ExitCode} for args: {string.Join(" ", args)}");
    }

    private async Task<BufferedCommandResult> ExecuteCheckedBufferedAsync(
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var result = await Cli.Wrap(_memvidExecutable)
            .WithArguments(args)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (!result.IsSuccess)
        {
            var error = string.IsNullOrWhiteSpace(result.StandardError)
                ? $"memvid exited with code {result.ExitCode} for args: {string.Join(" ", args)}"
                : result.StandardError.Trim();
            throw new InvalidOperationException(error);
        }

        return result;
    }
}
