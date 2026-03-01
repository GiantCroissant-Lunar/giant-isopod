using GiantIsopod.Contracts.Core;
using GiantIsopod.Contracts.Protocol.Runtime;

namespace GiantIsopod.Plugin.Process;

/// <summary>
/// Creates the correct IAgentRuntime implementation based on RuntimeConfig type.
/// </summary>
public static class RuntimeFactory
{
    public static IAgentRuntime Create(
        string agentId,
        RuntimeConfig config,
        ModelSpec? model,
        string workingDirectory,
        Dictionary<string, string>? extraEnv = null)
    {
        return config switch
        {
            CliRuntimeConfig cli => new CliAgentRuntime(agentId, cli, model, workingDirectory, extraEnv),
            ApiRuntimeConfig => new ApiAgentRuntime(agentId),
            SdkRuntimeConfig => new SdkAgentRuntime(agentId),
            _ => throw new InvalidOperationException($"Unknown runtime config type: {config.GetType().Name}")
        };
    }

    /// <summary>
    /// Merges an explicit model spec with a default from the runtime config.
    /// Explicit fields take precedence over defaults.
    /// </summary>
    public static ModelSpec? MergeModel(ModelSpec? @explicit, ModelSpec? @default)
    {
        if (@explicit is null) return @default;
        if (@default is null) return @explicit;

        return new ModelSpec(
            Provider: @explicit.Provider ?? @default.Provider,
            ModelId: @explicit.ModelId ?? @default.ModelId,
            Parameters: MergeParameters(@explicit.Parameters, @default.Parameters));
    }

    private static IReadOnlyDictionary<string, string>? MergeParameters(
        IReadOnlyDictionary<string, string>? @explicit,
        IReadOnlyDictionary<string, string>? @default)
    {
        if (@explicit is null) return @default;
        if (@default is null) return @explicit;

        var merged = new Dictionary<string, string>(@default);
        foreach (var (key, value) in @explicit)
            merged[key] = value;
        return merged;
    }
}
