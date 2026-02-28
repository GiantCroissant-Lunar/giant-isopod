using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CliWrap;
using CliWrap.EventStream;
using GiantIsopod.Contracts.Core;

namespace GiantIsopod.Plugin.Process;

/// <summary>
/// CliWrap-based pi --mode json -p process manager.
/// Runs pi in one-shot JSON mode with a prompt, streams JSONL events.
/// Accumulates word-level deltas into readable lines before emitting.
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
                "--mode", "json",
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
            })
            .WithValidation(CommandResultValidation.None);

        IsRunning = true;

        // Accumulate deltas into readable chunks
        var thinkBuf = new StringBuilder();
        var textBuf = new StringBuilder();

        await foreach (var cmdEvent in cmd.ListenAsync(linkedCts.Token))
        {
            if (cmdEvent is not StandardOutputCommandEvent stdOut || string.IsNullOrWhiteSpace(stdOut.Text))
                continue;

            var (eventType, content) = ParseJsonlEvent(stdOut.Text);

            switch (eventType)
            {
                case EventKind.Lifecycle:
                    // Flush any pending buffers first
                    if (thinkBuf.Length > 0) { yield return $"💭 {thinkBuf}"; thinkBuf.Clear(); }
                    if (textBuf.Length > 0) { yield return textBuf.ToString(); textBuf.Clear(); }
                    if (content != null) yield return content;
                    break;

                case EventKind.ThinkingDelta:
                    thinkBuf.Append(content);
                    // Flush on sentence boundaries or when buffer gets long
                    if (ShouldFlush(thinkBuf))
                    {
                        yield return $"💭 {thinkBuf}";
                        thinkBuf.Clear();
                    }
                    break;

                case EventKind.ThinkingEnd:
                    if (thinkBuf.Length > 0) { yield return $"💭 {thinkBuf}"; thinkBuf.Clear(); }
                    break;

                case EventKind.TextDelta:
                    textBuf.Append(content);
                    if (ShouldFlush(textBuf))
                    {
                        yield return textBuf.ToString();
                        textBuf.Clear();
                    }
                    break;

                case EventKind.TextEnd:
                    if (textBuf.Length > 0) { yield return textBuf.ToString(); textBuf.Clear(); }
                    break;

                case EventKind.Skip:
                    break;
            }
        }

        // Flush remaining
        if (thinkBuf.Length > 0) yield return $"💭 {thinkBuf}";
        if (textBuf.Length > 0) yield return textBuf.ToString();

        IsRunning = false;
    }

    /// <summary>
    /// Flush buffer on newlines or when buffer exceeds ~100 chars.
    /// Avoids splitting on periods mid-sentence (e.g. "Godot 4.6").
    /// </summary>
    private static bool ShouldFlush(StringBuilder buf)
    {
        if (buf.Length == 0) return false;
        if (buf.Length >= 100) return true;

        // Flush on newlines
        for (int i = buf.Length - 1; i >= 0; i--)
        {
            if (buf[i] == '\n') return true;
        }

        return false;
    }

    private enum EventKind { Skip, Lifecycle, ThinkingDelta, ThinkingEnd, TextDelta, TextEnd }

    private static (EventKind kind, string? content) ParseJsonlEvent(string jsonLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp))
                return (EventKind.Skip, null);

            var type = typeProp.GetString();

            return type switch
            {
                "session" => (EventKind.Lifecycle, "[session started]"),
                "agent_start" => (EventKind.Skip, null),
                "turn_start" => (EventKind.Skip, null),
                "turn_end" => (EventKind.Skip, null),
                "agent_end" => (EventKind.Lifecycle, "[finished]"),
                "message_start" => (EventKind.Skip, null),
                "message_end" => (EventKind.Skip, null),
                "message_update" => ParseMessageUpdate(root),
                _ => (EventKind.Skip, null)
            };
        }
        catch
        {
            return (EventKind.Lifecycle, jsonLine.Length > 120 ? jsonLine[..120] + "..." : jsonLine);
        }
    }


    private static (EventKind, string?) ParseMessageUpdate(JsonElement root)
    {
        if (!root.TryGetProperty("assistantMessageEvent", out var evt) ||
            !evt.TryGetProperty("type", out var evtType))
            return (EventKind.Skip, null);

        var t = evtType.GetString();

        return t switch
        {
            "thinking_start" => (EventKind.Skip, null),
            "thinking_delta" => (EventKind.ThinkingDelta, evt.TryGetProperty("delta", out var td) ? td.GetString() : null),
            "thinking_end" => (EventKind.ThinkingEnd, null),
            "text_start" => (EventKind.Skip, null),
            "text_delta" => (EventKind.TextDelta, evt.TryGetProperty("delta", out var txtD) ? txtD.GetString() : null),
            "text_end" => (EventKind.TextEnd, null),
            "tool_use_start" => (EventKind.Lifecycle, ExtractToolUse(evt)),
            "tool_result" => (EventKind.Lifecycle, "[tool result received]"),
            _ => (EventKind.Skip, null)
        };
    }

    private static string ExtractToolUse(JsonElement evt)
    {
        if (evt.TryGetProperty("toolUse", out var tu) &&
            tu.TryGetProperty("name", out var name))
            return $"🔧 tool_use: {name.GetString()}";
        return "🔧 [tool use]";
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
    }
}
