using System.Text.Json.Serialization;

namespace GiantIsopod.Contracts.Protocol.Runtime;

/// <summary>
/// Configuration for an SDK-based agent runtime (stub — not yet implemented).
/// </summary>
public record SdkRuntimeConfig : RuntimeConfig
{
    [JsonPropertyName("sdkName")]
    public required string SdkName { get; init; }

    [JsonPropertyName("options")]
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();
}
