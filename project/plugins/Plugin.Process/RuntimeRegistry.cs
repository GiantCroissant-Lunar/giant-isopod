using System.Text.Json;
using GiantIsopod.Contracts.Protocol.CliProvider;
using GiantIsopod.Contracts.Protocol.Runtime;

namespace GiantIsopod.Plugin.Process;

/// <summary>
/// Loads runtime definitions from runtimes.json (polymorphic) or legacy cli-providers.json.
/// Resolves runtimes by id.
/// </summary>
public sealed class RuntimeRegistry
{
    private readonly Dictionary<string, RuntimeConfig> _runtimes = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<RuntimeConfig> All => _runtimes.Values;

    public static RuntimeRegistry LoadFromJson(string json)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        var root = JsonSerializer.Deserialize<RuntimesRoot>(json, options)
            ?? throw new InvalidOperationException("Failed to deserialize runtimes.json");

        var registry = new RuntimeRegistry();
        foreach (var entry in root.Runtimes)
            registry._runtimes[entry.Id] = entry;
        return registry;
    }

    /// <summary>
    /// Loads from legacy cli-providers.json format, converting CliProviderEntry → CliRuntimeConfig.
    /// </summary>
    public static RuntimeRegistry LoadFromLegacyCliProviders(string json)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var root = JsonSerializer.Deserialize<CliProvidersRoot>(json, options)
            ?? throw new InvalidOperationException("Failed to deserialize cli-providers.json");

        var registry = new RuntimeRegistry();
        foreach (var entry in root.Providers)
        {
            registry._runtimes[entry.Id] = new CliRuntimeConfig
            {
                Id = entry.Id,
                DisplayName = entry.DisplayName,
                Executable = entry.Executable,
                Args = entry.Args,
                Env = entry.Env,
                Defaults = entry.Defaults
            };
        }
        return registry;
    }

    public RuntimeConfig? Resolve(string runtimeId)
        => _runtimes.GetValueOrDefault(runtimeId);

    public RuntimeConfig ResolveOrDefault(string? runtimeId = null)
        => (runtimeId != null ? Resolve(runtimeId) : null)
           ?? _runtimes.Values.FirstOrDefault()
           ?? throw new InvalidOperationException("No runtimes configured");
}
