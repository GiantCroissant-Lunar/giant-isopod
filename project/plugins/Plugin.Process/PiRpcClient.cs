using System.Runtime.CompilerServices;
using CliWrap;
using CliWrap.EventStream;
using GiantIsopod.Contracts.Core;

namespace GiantIsopod.Plugin.Process;

/// <summary>
/// CliWrap-based pi --mode text process manager.
/// Runs pi in text mode, streams raw terminal output (with ANSI escape sequences).
/// Output is forwarded directly to the GodotXterm Terminal node for rendering.
/// </summary>
public sealed class PiRpcClient : IAgentProcess
{
    private readonly string _piExecutable;
    private readonly string _workingDirectory;
    private readonly string _provider;
    private readonly string _model;
    private readonly Dictionary<string, string> _environment;
    private CancellationTokenSource? _cts;
    private string _prompt = "Explore the current directory, read key files, and suggest improvements.";

    public string AgentId { get; }
    public bool IsRunning { get; private set; }

    public PiRpcClient(string agentId, string piExecutable, string workingDirectory,
        string provider = "zai", string model = "glm-4.7", Dictionary<string, string>? environment = null)
    {
        AgentId = agentId;
        _piExecutable = piExecutable;
        _workingDirectory = workingDirectory;
        _provider = provider;
        _model = model;
        _environment = environment ?? new();
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

        var cmd = Cli.Wrap(_piExecutable)
            .WithArguments([
                "--mode", "text",
                "--no-session",
                "--provider", _provider,
                "--model", _model,
                "-p", _prompt
            ])
            .WithWorkingDirectory(_workingDirectory)
            .WithEnvironmentVariables(env =>
            {
                foreach (var (key, value) in _environment)
                    env.Set(key, value);
                // Tell pi it's in a color terminal
                env.Set("COLORTERM", "truecolor");
                env.Set("TERM", "xterm-256color");
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

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
    }
}
