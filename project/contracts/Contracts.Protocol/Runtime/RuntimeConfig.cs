using System.Text.Json.Serialization;
using GiantIsopod.Contracts.Core;

namespace GiantIsopod.Contracts.Protocol.Runtime;

/// <summary>
/// Base configuration for an agent runtime. Discriminated by "type" in JSON.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CliRuntimeConfig), "cli")]
[JsonDerivedType(typeof(ApiRuntimeConfig), "api")]
[JsonDerivedType(typeof(SdkRuntimeConfig), "sdk")]
public abstract record RuntimeConfig
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("defaultModel")]
    public ModelSpec? DefaultModel { get; init; }
}
