using System.Runtime.CompilerServices;
using CliWrap;
using CliWrap.EventStream;
using GiantIsopod.Contracts.Core;
using GiantIsopod.Contracts.Protocol.CliProvider;

namespace GiantIsopod.Plugin.Process;

/// <summary>
/// Generic CLI agent process driven by CliProviderEntry config.
/// Resolves {prompt}, {provider}, {model} placeholders in args at runtime.
/// Streams stdout/stderr line-by-line via CliWrap ListenAsync.
/// </summary>
public sealed class CliAgentProcess : IAgentProcess
{
    private readonly CliProviderEntry _provider;
    private readonly string _workingDirectory;
    private readonly Dictionary<string, string> _extraEnv;
    private CancellationTokenSource? _cts;
    private string _prompt = "Explore the current directory, read key files, and suggest improvements.";

    public string AgentId { get; }
    public bool IsRunning { get; private set; }
    public string ProviderId => _provider.Id;

    public CliAgentProcess(
        string agentId,
        CliProviderEntry provider,
        string workingDirectory,
        Dictionary<string, string>? extraEnv = null)
    {
        AgentId = agentId;
        _provider = provider;
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

        var cmd = Cli.Wrap(_provider.Executable)
            .WithArguments(resolvedArgs)
            .WithWorkingDirectory(_workingDirectory)
            .WithEnvironmentVariables(env =>
            {
                // Provider-level env from JSON config, with placeholder resolution
                // e.g. {ZAI_API_KEY} in config gets resolved from _extraEnv
                foreach (var (key, value) in _provider.Env)
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

    /// <summary>
    /// Resolves {prompt}, {provider}, {model} and any defaults-defined placeholders in args.
    /// </summary>
    private string[] ResolveArgs()
    {
        var placeholders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["prompt"] = _prompt
        };

        // Merge defaults from provider config
        foreach (var (key, value) in _provider.Defaults)
            placeholders.TryAdd(key, value);

        return _provider.Args
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
