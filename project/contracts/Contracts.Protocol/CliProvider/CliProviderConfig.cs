using System.Text.Json.Serialization;

namespace GiantIsopod.Contracts.Protocol.CliProvider;

/// <summary>
/// Root config loaded from cli-providers.json.
/// Defines available CLI coding agents and how to invoke them.
/// </summary>
public record CliProvidersRoot
{
    [JsonPropertyName("providers")]
    public IReadOnlyList<CliProviderEntry> Providers { get; init; } = [];
}

/// <summary>
/// A single CLI provider entry — executable, argument template, env vars, defaults.
/// Argument templates support {prompt}, {provider}, {model} placeholders.
/// </summary>
public record CliProviderEntry
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("executable")]
    public required string Executable { get; init; }

    [JsonPropertyName("args")]
    public IReadOnlyList<string> Args { get; init; } = [];

    [JsonPropertyName("env")]
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();

    [JsonPropertyName("defaults")]
    public IReadOnlyDictionary<string, string> Defaults { get; init; } = new Dictionary<string, string>();
}
