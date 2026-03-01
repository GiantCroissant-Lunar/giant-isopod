using System.Text.Json.Serialization;

namespace GiantIsopod.Contracts.Protocol.Runtime;

/// <summary>
/// Configuration for a CLI-based agent runtime (e.g., pi, kilo, codex, kimi).
/// </summary>
public record CliRuntimeConfig : RuntimeConfig
{
    [JsonPropertyName("executable")]
    public required string Executable { get; init; }

    [JsonPropertyName("args")]
    public IReadOnlyList<string> Args { get; init; } = [];

    [JsonPropertyName("env")]
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();

    [JsonPropertyName("defaults")]
    public IReadOnlyDictionary<string, string> Defaults { get; init; } = new Dictionary<string, string>();
}
