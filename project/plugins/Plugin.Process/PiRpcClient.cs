using System.Runtime.CompilerServices;
using System.Text.Json;
using CliWrap;
using CliWrap.EventStream;
using GiantIsopod.Contracts.Core;

namespace GiantIsopod.Plugin.Process;

/// <summary>
/// CliWrap-based pi --mode json -p process manager.
/// Runs pi in one-shot JSON mode with a prompt, streams JSONL events.
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
        // Store prompt for next ReadEventsAsync call
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

        await foreach (var cmdEvent in cmd.ListenAsync(linkedCts.Token))
        {
            if (cmdEvent is StandardOutputCommandEvent stdOut && !string.IsNullOrWhiteSpace(stdOut.Text))
            {
                // Parse JSONL and extract meaningful content
                var parsed = ParseJsonlEvent(stdOut.Text);
                if (parsed != null)
                    yield return parsed;
            }
        }

        IsRunning = false;
    }

    /// <summary>
    /// Parses a pi JSONL event line and extracts human-readable content.
    /// Returns null for events that don't produce visible output.
    /// </summary>
    private static string? ParseJsonlEvent(string jsonLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp))
                return null;

            var type = typeProp.GetString();

            return type switch
            {
                "session" => "[session started]",
                "agent_start" => "[agent started]",
                "turn_start" => "[turn started]",

                "message_update" => ExtractMessageUpdate(root),

                "message_end" => null, // redundant with deltas
                "turn_end" => "[turn complete]",
                "agent_end" => "[agent finished]",

                "message_start" => ExtractMessageStart(root),

                _ => null
            };
        }
        catch
        {
            // Not valid JSON, return raw
            return jsonLine.Length > 120 ? jsonLine[..120] + "..." : jsonLine;
        }
    }

    private static string? ExtractMessageStart(JsonElement root)
    {
        if (root.TryGetProperty("message", out var msg) &&
            msg.TryGetProperty("role", out var role))
        {
            var r = role.GetString();
            if (r == "user") return null; // we already know the prompt
            if (r == "assistant") return "[assistant responding...]";
        }
        return null;
    }

    private static string? ExtractMessageUpdate(JsonElement root)
    {
        if (!root.TryGetProperty("assistantMessageEvent", out var evt))
            return null;

        if (!evt.TryGetProperty("type", out var evtType))
            return null;

        var t = evtType.GetString();

        switch (t)
        {
            case "thinking_start":
                return "[thinking...]";

            case "thinking_delta":
                if (evt.TryGetProperty("delta", out var td))
                    return $"  💭 {td.GetString()}";
                return null;

            case "thinking_end":
                return "[thinking complete]";

            case "text_start":
                return null; // text_delta will follow

            case "text_delta":
                if (evt.TryGetProperty("delta", out var txtD))
                    return txtD.GetString();
                return null;

            case "text_end":
                return null;

            case "tool_use_start":
                return ExtractToolUse(evt);

            case "tool_result":
                return "[tool result received]";

            default:
                return null;
        }
    }

    private static string? ExtractToolUse(JsonElement evt)
    {
        // Try to extract tool name from the event
        if (evt.TryGetProperty("toolUse", out var tu) &&
            tu.TryGetProperty("name", out var name))
        {
            return $"🔧 tool_use: {name.GetString()}";
        }
        return "🔧 [tool use]";
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
    }
}
