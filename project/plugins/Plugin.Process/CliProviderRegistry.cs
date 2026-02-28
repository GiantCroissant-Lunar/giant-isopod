using System.Text.Json;
using GiantIsopod.Contracts.Protocol.CliProvider;

namespace GiantIsopod.Plugin.Process;

/// <summary>
/// Loads CLI provider definitions from cli-providers.json and resolves by id.
/// </summary>
public sealed class CliProviderRegistry
{
    private readonly Dictionary<string, CliProviderEntry> _providers = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<CliProviderEntry> All => _providers.Values;

    public static CliProviderRegistry LoadFromJson(string json)
    {
        var root = JsonSerializer.Deserialize<CliProvidersRoot>(json)
            ?? throw new InvalidOperationException("Failed to deserialize cli-providers.json");

        var registry = new CliProviderRegistry();
        foreach (var entry in root.Providers)
            registry._providers[entry.Id] = entry;
        return registry;
    }

    public CliProviderEntry? Resolve(string providerId)
        => _providers.GetValueOrDefault(providerId);

    public CliProviderEntry ResolveOrDefault(string? providerId = null)
        => (providerId != null ? Resolve(providerId) : null)
           ?? _providers.Values.FirstOrDefault()
           ?? throw new InvalidOperationException("No CLI providers configured");
}
