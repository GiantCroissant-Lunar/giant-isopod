using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using CliWrap;
using GiantIsopod.Contracts.Core;

namespace GiantIsopod.Plugin.Process;

/// <summary>
/// CliWrap-based pi --mode text process manager.
/// Runs pi in text mode, streams raw terminal output preserving ANSI escape sequences.
/// Uses raw byte streaming to preserve ANSI color codes that line-based APIs strip.
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
        var token = linkedCts.Token;

        var chunks = Channel.CreateUnbounded<string>();

        // Custom stream that captures raw bytes and converts to string chunks
        var captureStream = new CallbackStream(async (buffer, offset, count) =>
        {
            var text = Encoding.UTF8.GetString(buffer, offset, count);
            if (!string.IsNullOrEmpty(text))
                await chunks.Writer.WriteAsync(text, token);
        });

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
                env.Set("COLORTERM", "truecolor");
                env.Set("TERM", "xterm-256color");
                env.Set("FORCE_COLOR", "1");
            })
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToStream(captureStream))
            .WithStandardErrorPipe(PipeTarget.ToStream(captureStream));

        IsRunning = true;

        // Run command in background
        _ = Task.Run(async () =>
        {
            try { await cmd.ExecuteAsync(token); }
            catch (OperationCanceledException) { }
            finally { chunks.Writer.TryComplete(); }
        }, token);

        // Yield raw chunks preserving ANSI sequences
        await foreach (var chunk in chunks.Reader.ReadAllAsync(token))
        {
            yield return chunk;
        }

        IsRunning = false;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
    }
}

/// <summary>
/// Stream that invokes a callback on every Write, used to capture raw process output bytes.
/// </summary>
internal sealed class CallbackStream : Stream
{
    private readonly Func<byte[], int, int, Task> _onWrite;

    public CallbackStream(Func<byte[], int, int, Task> onWrite) => _onWrite = onWrite;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        _onWrite(buffer, offset, count).GetAwaiter().GetResult();

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        await _onWrite(buffer, offset, count);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
        await _onWrite(buffer.ToArray(), 0, buffer.Length);

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
