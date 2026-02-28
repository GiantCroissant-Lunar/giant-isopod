using System.Runtime.CompilerServices;
using System.Text;
using CliWrap;
using CliWrap.EventStream;
using GiantIsopod.Contracts.Core;

namespace GiantIsopod.Plugin.Process;

/// <summary>
/// CliWrap-based pi --mode rpc process manager.
/// </summary>
public sealed class PiRpcClient : IAgentProcess
{
    private readonly string _piExecutable;
    private readonly string _workingDirectory;
    private readonly string _provider;
    private readonly string _model;
    private readonly Dictionary<string, string> _environment;
    private CommandTask<CommandResult>? _task;
    private PipeSource? _stdinPipe;
    private StreamWriter? _stdinWriter;
    private CancellationTokenSource? _cts;

    public string AgentId { get; }
    public bool IsRunning => _task is { Task.IsCompleted: false };

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
        // Process is started lazily when ReadEventsAsync is called
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        _cts?.Cancel();
        _stdinWriter?.Dispose();
        if (_task != null)
        {
            try { await _task; } catch (OperationCanceledException) { }
        }
    }

    public async Task SendAsync(string message, CancellationToken ct = default)
    {
        if (_stdinWriter != null)
        {
            await _stdinWriter.WriteLineAsync(message.AsMemory(), ct);
            await _stdinWriter.FlushAsync(ct);
        }
    }

    public async IAsyncEnumerable<string> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts?.Token ?? CancellationToken.None);

        var cmd = Cli.Wrap(_piExecutable)
            .WithArguments(["--mode", "rpc", "--no-session", "--provider", _provider, "--model", _model])
            .WithWorkingDirectory(_workingDirectory)
            .WithEnvironmentVariables(env =>
            {
                foreach (var (key, value) in _environment)
                    env.Set(key, value);
            })
            .WithValidation(CommandResultValidation.None);

        await foreach (var cmdEvent in cmd.ListenAsync(linkedCts.Token))
        {
            if (cmdEvent is StandardOutputCommandEvent stdOut)
            {
                yield return stdOut.Text;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
    }
}
