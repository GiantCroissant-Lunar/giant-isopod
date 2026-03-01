using System.Runtime.CompilerServices;
using CliWrap;
using CliWrap.EventStream;
using GiantIsopod.Contracts.Core;
using GiantIsopod.Contracts.Protocol.Runtime;

namespace GiantIsopod.Plugin.Process;

/// <summary>
/// CLI-based agent runtime driven by CliRuntimeConfig.
/// Resolves {prompt}, {provider}, {model} placeholders in args at runtime.
/// Streams stdout/stderr line-by-line via CliWrap ListenAsync.
/// </summary>
public sealed class CliAgentRuntime : IAgentRuntime
{
    private readonly CliRuntimeConfig _config;
    private readonly ModelSpec? _model;
    private readonly string _workingDirectory;
    private readonly Dictionary<string, string> _extraEnv;
    private CancellationTokenSource? _cts;
    private string _prompt = "Explore the current directory, read key files, and suggest improvements.";

    public string AgentId { get; }
    public bool IsRunning { get; private set; }

    public CliAgentRuntime(
        string agentId,
        CliRuntimeConfig config,
        ModelSpec? model,
        string workingDirectory,
        Dictionary<string, string>? extraEnv = null)
    {
        AgentId = agentId;
        _config = config;
        _model = model;
        _workingDirectory = workingDirectory;
        _extraEnv = extraEnv ?? new();
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        _cts?.Cancel();
        IsRunning = false;
    }

    public Task SendAsync(string message, CancellationToken ct = default)
    {
        _prompt = message;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts?.Token ?? CancellationToken.None);

        var resolvedArgs = ResolveArgs();

        var cmd = Cli.Wrap(_config.Executable)
            .WithArguments(resolvedArgs)
            .WithWorkingDirectory(_workingDirectory)
            .WithEnvironmentVariables(env =>
            {
                foreach (var (key, value) in _config.Env)
                    env.Set(key, ResolvePlaceholders(value, _extraEnv));
            })
            .WithValidation(CommandResultValidation.None);

        IsRunning = true;

        await foreach (var cmdEvent in cmd.ListenAsync(linkedCts.Token))
        {
            switch (cmdEvent)
            {
                case StandardOutputCommandEvent stdOut:
                    if (!string.IsNullOrEmpty(stdOut.Text))
                        yield return stdOut.Text;
                    break;
                case StandardErrorCommandEvent stdErr:
                    if (!string.IsNullOrEmpty(stdErr.Text))
                        yield return stdErr.Text;
                    break;
            }
        }

        IsRunning = false;
    }

    private string[] ResolveArgs()
    {
        var placeholders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["prompt"] = _prompt
        };

        // Merge defaults from config
        foreach (var (key, value) in _config.Defaults)
            placeholders.TryAdd(key, value);

        // Override with explicit model spec if provided
        var effectiveModel = RuntimeFactory.MergeModel(_model, _config.DefaultModel);
        if (effectiveModel?.Provider is { } provider)
            placeholders["provider"] = provider;
        if (effectiveModel?.ModelId is { } modelId)
            placeholders["model"] = modelId;

        return _config.Args
            .Select(arg => ResolvePlaceholders(arg, placeholders))
            .ToArray();
    }

    private static string ResolvePlaceholders(string template, Dictionary<string, string> values)
    {
        var result = template;
        foreach (var (key, value) in values)
            result = result.Replace($"{{{key}}}", value, StringComparison.OrdinalIgnoreCase);
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
    }
}
