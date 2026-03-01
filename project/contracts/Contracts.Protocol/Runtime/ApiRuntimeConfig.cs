using System.Text.Json.Serialization;

namespace GiantIsopod.Contracts.Protocol.Runtime;

/// <summary>
/// Configuration for an HTTP/API-based agent runtime (stub — not yet implemented).
/// </summary>
public record ApiRuntimeConfig : RuntimeConfig
{
    [JsonPropertyName("baseUrl")]
    public required string BaseUrl { get; init; }

    [JsonPropertyName("apiKeyEnvVar")]
    public string? ApiKeyEnvVar { get; init; }
}
