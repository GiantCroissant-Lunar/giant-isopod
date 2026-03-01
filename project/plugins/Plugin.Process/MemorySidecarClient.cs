using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;

namespace GiantIsopod.Plugin.Process;

/// <summary>
/// CliWrap-based wrapper for the memory-sidecar Python CLI.
/// Provides codebase indexing, code search, knowledge store/query.
/// </summary>
public sealed class MemorySidecarClient
{
    private readonly string _executable;
    private readonly string _dataDir;

    public MemorySidecarClient(string dataDir, string executable = "memory-sidecar")
    {
        _dataDir = dataDir;
        _executable = executable;
    }

    public async Task<IndexStats> IndexCodebaseAsync(string sourcePath, CancellationToken ct = default)
    {
        var result = await Cli.Wrap(_executable)
            .WithArguments(["index", sourcePath, "--db", CodebaseDbPath()])
            .WithEnvironmentVariables(e => e.Set("MEMORY_SIDECAR_DATA_DIR", _dataDir))
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (!result.IsSuccess)
            return new IndexStats(0, 0, 0);

        return JsonSerializer.Deserialize<IndexStats>(result.StandardOutput)
            ?? new IndexStats(0, 0, 0);
    }

    public async Task<IReadOnlyList<CodeSearchResult>> SearchCodeAsync(
        string query, int topK = 10, CancellationToken ct = default)
    {
        var result = await Cli.Wrap(_executable)
            .WithArguments(["search", query, "--db", CodebaseDbPath(),
                "--top-k", topK.ToString(), "--json-output"])
            .WithEnvironmentVariables(e => e.Set("MEMORY_SIDECAR_DATA_DIR", _dataDir))
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.StandardOutput))
            return [];

        return JsonSerializer.Deserialize<List<CodeSearchResult>>(result.StandardOutput) ?? [];
    }

    public async Task<int> StoreKnowledgeAsync(
        string agentId, string content, string category,
        IDictionary<string, string>? tags = null, CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "store", content, "--agent", agentId,
            "--category", category, "--db", KnowledgeDbPath(agentId)
        };
        if (tags != null)
        {
            foreach (var (key, value) in tags)
            {
                args.Add("--tag");
                args.Add($"{key}:{value}");
            }
        }

        var result = await Cli.Wrap(_executable)
            .WithArguments(args)
            .WithEnvironmentVariables(e => e.Set("MEMORY_SIDECAR_DATA_DIR", _dataDir))
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (!result.IsSuccess)
            return -1;

        var response = JsonSerializer.Deserialize<StoreResponse>(result.StandardOutput);
        return response?.Id ?? -1;
    }

    public async Task<IReadOnlyList<KnowledgeSearchResult>> SearchKnowledgeAsync(
        string agentId, string query, string? category = null,
        int topK = 10, CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "query", query, "--agent", agentId,
            "--top-k", topK.ToString(), "--db", KnowledgeDbPath(agentId), "--json-output"
        };
        if (category != null)
        {
            args.Add("--category");
            args.Add(category);
        }

        var result = await Cli.Wrap(_executable)
            .WithArguments(args)
            .WithEnvironmentVariables(e => e.Set("MEMORY_SIDECAR_DATA_DIR", _dataDir))
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.StandardOutput))
            return [];

        return JsonSerializer.Deserialize<List<KnowledgeSearchResult>>(result.StandardOutput) ?? [];
    }

    private string CodebaseDbPath() => Path.Combine(_dataDir, "codebase.sqlite");
    private string KnowledgeDbPath(string agentId) => Path.Combine(_dataDir, "knowledge", $"{agentId}.sqlite");
}

// ── Response types ──

public record IndexStats(
    [property: System.Text.Json.Serialization.JsonPropertyName("files_processed")] int FilesProcessed,
    [property: System.Text.Json.Serialization.JsonPropertyName("chunks_indexed")] int ChunksIndexed,
    [property: System.Text.Json.Serialization.JsonPropertyName("chunks_deleted")] int ChunksDeleted);

public record CodeSearchResult(
    [property: System.Text.Json.Serialization.JsonPropertyName("filename")] string Filename,
    [property: System.Text.Json.Serialization.JsonPropertyName("location")] string Location,
    [property: System.Text.Json.Serialization.JsonPropertyName("language")] string? Language,
    [property: System.Text.Json.Serialization.JsonPropertyName("code")] string Code,
    [property: System.Text.Json.Serialization.JsonPropertyName("score")] double Score);

public record KnowledgeSearchResult(
    [property: System.Text.Json.Serialization.JsonPropertyName("content")] string Content,
    [property: System.Text.Json.Serialization.JsonPropertyName("category")] string Category,
    [property: System.Text.Json.Serialization.JsonPropertyName("tags")] Dictionary<string, string>? Tags,
    [property: System.Text.Json.Serialization.JsonPropertyName("stored_at")] string StoredAt,
    [property: System.Text.Json.Serialization.JsonPropertyName("relevance")] double Relevance);

internal record StoreResponse(
    [property: System.Text.Json.Serialization.JsonPropertyName("id")] int Id);
