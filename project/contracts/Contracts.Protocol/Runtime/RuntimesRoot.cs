using System.Text.Json.Serialization;

namespace GiantIsopod.Contracts.Protocol.Runtime;

/// <summary>
/// Root config loaded from runtimes.json.
/// </summary>
public record RuntimesRoot
{
    [JsonPropertyName("runtimes")]
    public IReadOnlyList<RuntimeConfig> Runtimes { get; init; } = [];
}
