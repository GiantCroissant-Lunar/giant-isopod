namespace GiantIsopod.Contracts.Core;

/// <summary>
/// Specifies which model powers an agent. Provider-agnostic.
/// </summary>
public record ModelSpec(
    string? Provider = null,
    string? ModelId = null,
    IReadOnlyDictionary<string, string>? Parameters = null);
