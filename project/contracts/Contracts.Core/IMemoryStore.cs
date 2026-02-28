namespace GiantIsopod.Contracts.Core;

/// <summary>
/// Per-agent persistent memory backed by Memvid .mv2 files.
/// </summary>
public interface IMemoryStore
{
    string AgentId { get; }
    string FilePath { get; }

    Task PutAsync(string content, string? title = null, IDictionary<string, string>? tags = null, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryHit>> SearchAsync(string query, int topK = 10, CancellationToken ct = default);
    Task CommitAsync(CancellationToken ct = default);
}

public record MemoryHit(string Text, string? Title, float Score, DateTimeOffset? Timestamp);
