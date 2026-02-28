using System.Text.Json.Serialization;

namespace GiantIsopod.Contracts.Protocol.Memvid;

/// <summary>
/// Memvid CLI response types. Will be replaced by quicktype-generated types.
/// </summary>
public record MemvidSearchResponse
{
    [JsonPropertyName("hits")]
    public IReadOnlyList<MemvidHit> Hits { get; init; } = [];
}

public record MemvidHit
{
    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("score")]
    public float Score { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; init; }
}
