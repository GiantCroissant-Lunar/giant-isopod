using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using GiantIsopod.Contracts.Core;
using GiantIsopod.Contracts.Protocol.Runtime;
using ProcessHandle = System.Diagnostics.Process;

namespace GiantIsopod.Plugin.Process;

/// <summary>
/// Kimi wire-mode runtime backed by JSON-RPC 2.0 over stdin/stdout.
/// </summary>
public sealed class KimiWireAgentRuntime : IAgentRuntime
{
    private static readonly JsonWriterOptions JsonWriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly KimiWireRuntimeConfig _config;
    private readonly ModelSpec? _model;
    private readonly string _workingDirectory;
    private readonly Dictionary<string, string> _extraEnv;
    private CancellationTokenSource? _cts;
    private string _prompt = "Explore the current directory, read key files, and suggest improvements.";

    public string AgentId { get; }
    public bool IsRunning { get; private set; }

    public KimiWireAgentRuntime(
        string agentId,
        KimiWireRuntimeConfig config,
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

    public Task StopAsync(CancellationToken ct = default)
    {
        _cts?.Cancel();
        IsRunning = false;
        return Task.CompletedTask;
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
        var commandContext = ResolveCommandContext();
        var traceFilePath = CreateTraceFilePath();
        using var traceWriter = new StreamWriter(traceFilePath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        using var process = CreateProcess(commandContext);

        try
        {
            await WriteTraceAsync(traceWriter, "meta", $"agent={AgentId} runtime={_config.Id} cwd={_workingDirectory}");
            yield return $"[runtime-trace] file={traceFilePath}";
            yield return BuildLaunchDiagnostic(commandContext);

            process.Start();
            IsRunning = true;

            var output = Channel.CreateUnbounded<string>();
            var promptCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var initializeId = Guid.NewGuid().ToString("D");
            var promptId = Guid.NewGuid().ToString("D");

            var stdoutTask = ConsumeStdoutAsync(process, output.Writer, traceWriter, initializeId, promptId, promptCompletion, linkedCts.Token);
            var stderrTask = ConsumeStderrAsync(process, output.Writer, traceWriter, linkedCts.Token);

            await SendWireRequestAsync(process.StandardInput, BuildInitializeRequest(initializeId), traceWriter, linkedCts.Token);
            await SendWireRequestAsync(process.StandardInput, BuildPromptRequest(promptId), traceWriter, linkedCts.Token);

            _ = Task.Run(async () =>
            {
                try
                {
                    await promptCompletion.Task.WaitAsync(linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                await WriteTraceAsync(traceWriter, "meta", "prompt response completed; shutting down kimi wire process");
                TryTerminateProcess(process);
            }, CancellationToken.None);

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAll(stdoutTask, stderrTask);
                    output.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    output.Writer.TryComplete(ex);
                }
            }, CancellationToken.None);

            await foreach (var line in output.Reader.ReadAllAsync(linkedCts.Token))
                yield return line;
        }
        finally
        {
            if (linkedCts.IsCancellationRequested)
                await WriteTraceAsync(traceWriter, "meta", "runtime stream cancelled");

            await traceWriter.FlushAsync();
            IsRunning = false;
            linkedCts.Dispose();
            commandContext.Dispose();
        }
    }

    internal static string BuildInitializeRequest(string id)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer, JsonWriterOptions);
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");
        writer.WriteString("id", id);
        writer.WriteString("method", "initialize");
        writer.WriteStartObject("params");
        writer.WriteString("protocol_version", "1.3");
        writer.WriteStartObject("client");
        writer.WriteString("name", "giant-isopod");
        writer.WriteString("version", "1.0");
        writer.WriteEndObject();
        writer.WriteStartObject("capabilities");
        writer.WriteBoolean("supports_question", false);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal string BuildPromptRequest(string id)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer, JsonWriterOptions);
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");
        writer.WriteString("id", id);
        writer.WriteString("method", "prompt");
        writer.WriteStartObject("params");
        writer.WriteString("user_input", _prompt);

        var effectiveModel = RuntimeFactory.MergeModel(_model, _config.DefaultModel);
        if (effectiveModel?.ModelId is { Length: > 0 } modelId ||
            effectiveModel?.Provider is { Length: > 0 })
        {
            writer.WriteStartObject("model");
            if (effectiveModel.Provider is { Length: > 0 } provider)
                writer.WriteString("provider", provider);
            if (effectiveModel.ModelId is { Length: > 0 } concreteModelId)
                writer.WriteString("id", concreteModelId);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static IReadOnlyList<string> ExtractOutputLines(string jsonLine)
    {
        using var document = JsonDocument.Parse(jsonLine);
        return ExtractOutputLines(document.RootElement);
    }

    internal static bool TryExtractContentFragment(string jsonLine, out string fragment)
    {
        using var document = JsonDocument.Parse(jsonLine);
        return TryExtractContentFragment(document.RootElement, out fragment);
    }

    internal static IReadOnlyList<string> ExtractOutputLines(JsonElement root)
    {
        var lines = new List<string>();

        if (root.TryGetProperty("method", out var methodProperty) &&
            methodProperty.ValueKind == JsonValueKind.String &&
            string.Equals(methodProperty.GetString(), "event", StringComparison.OrdinalIgnoreCase) &&
            root.TryGetProperty("params", out var eventParams))
        {
            var eventKind = GetEventKind(eventParams);
            switch (eventKind)
            {
                case "ContentPart":
                    if (TryExtractContentText(eventParams, out var contentText))
                        lines.Add(contentText);
                    break;
                case "ToolCall":
                    lines.Add($"[kimi-wire] tool_call {DescribeToolName(eventParams)}");
                    break;
                case "ToolResult":
                    lines.Add($"[kimi-wire] tool_result {DescribeToolResult(eventParams)}");
                    break;
                case "StepBegin":
                    lines.Add($"[kimi-wire] step_begin {DescribeStep(eventParams)}");
                    break;
                case "TurnBegin":
                    lines.Add("[kimi-wire] turn_begin");
                    break;
                case "TurnEnd":
                    lines.Add("[kimi-wire] turn_end");
                    break;
            }
        }

        if (root.TryGetProperty("error", out var errorProperty) &&
            errorProperty.ValueKind == JsonValueKind.Object)
        {
            var message = errorProperty.TryGetProperty("message", out var errorMessage)
                ? errorMessage.GetString()
                : "Unknown Kimi wire error";
            lines.Add($"[kimi-wire] error {message}");
        }

        return lines;
    }

    internal static bool TryExtractContentFragment(JsonElement root, out string fragment)
    {
        fragment = string.Empty;

        if (!root.TryGetProperty("method", out var methodProperty) ||
            methodProperty.ValueKind != JsonValueKind.String ||
            !string.Equals(methodProperty.GetString(), "event", StringComparison.OrdinalIgnoreCase) ||
            !root.TryGetProperty("params", out var eventParams))
        {
            return false;
        }

        if (!string.Equals(GetEventKind(eventParams), "ContentPart", StringComparison.Ordinal))
            return false;

        return TryExtractContentText(eventParams, out fragment);
    }

    internal static bool TryBuildResponseJson(string jsonLine, out string? responseJson)
    {
        using var document = JsonDocument.Parse(jsonLine);
        return TryBuildResponseJson(document.RootElement, out responseJson);
    }

    internal static bool TryBuildResponseJson(JsonElement root, out string? responseJson)
    {
        responseJson = null;

        if (!root.TryGetProperty("method", out var methodProperty) || methodProperty.ValueKind != JsonValueKind.String)
            return false;
        if (!string.Equals(methodProperty.GetString(), "request", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!root.TryGetProperty("id", out var idProperty) || idProperty.ValueKind != JsonValueKind.String)
            return false;
        if (!root.TryGetProperty("params", out var requestParams) || requestParams.ValueKind != JsonValueKind.Object)
            return false;
        if (!requestParams.TryGetProperty("type", out var typeProperty) || typeProperty.ValueKind != JsonValueKind.String)
            return false;
        if (!requestParams.TryGetProperty("payload", out var payloadProperty) || payloadProperty.ValueKind != JsonValueKind.Object)
            return false;

        var requestType = typeProperty.GetString() ?? string.Empty;
        var idJson = idProperty.GetRawText();

        if (string.Equals(requestType, "ApprovalRequest", StringComparison.OrdinalIgnoreCase) &&
            payloadProperty.TryGetProperty("id", out var approvalIdProperty))
        {
            var requestIdJson = approvalIdProperty.GetRawText();
            responseJson = $"{{\"jsonrpc\":\"2.0\",\"id\":{idJson},\"result\":{{\"request_id\":{requestIdJson},\"response\":\"approve\"}}}}";
            return true;
        }

        if (string.Equals(requestType, "QuestionRequest", StringComparison.OrdinalIgnoreCase) &&
            payloadProperty.TryGetProperty("id", out var questionIdProperty))
        {
            var requestIdJson = questionIdProperty.GetRawText();
            responseJson = $"{{\"jsonrpc\":\"2.0\",\"id\":{idJson},\"result\":{{\"request_id\":{requestIdJson},\"answers\":{{}}}}}}";
            return true;
        }

        if (string.Equals(requestType, "ToolCallRequest", StringComparison.OrdinalIgnoreCase) &&
            payloadProperty.TryGetProperty("id", out var toolCallIdProperty))
        {
            var toolCallIdJson = toolCallIdProperty.GetRawText();
            responseJson = $"{{\"jsonrpc\":\"2.0\",\"id\":{idJson},\"result\":{{\"tool_call_id\":{toolCallIdJson},\"return_value\":{{\"is_error\":true,\"output\":\"\",\"message\":\"External tool requests are not supported by Giant Isopod Kimi wire runtime.\",\"display\":[]}}}}}}";
            return true;
        }

        return false;
    }

    private ProcessHandle CreateProcess(ResolvedCommandContext context)
    {
        var executable = CliExecutableResolver.Resolve(_config.Executable);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = _workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };

        foreach (var arg in context.Args)
            startInfo.ArgumentList.Add(arg);

        foreach (var (key, value) in _config.Env)
            startInfo.Environment[key] = ResolvePlaceholders(value, context.EnvironmentPlaceholders);

        return new ProcessHandle
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
    }

    private async Task ConsumeStdoutAsync(
        ProcessHandle process,
        ChannelWriter<string> output,
        StreamWriter traceWriter,
        string initializeId,
        string promptId,
        TaskCompletionSource<bool> promptCompletion,
        CancellationToken ct)
    {
        var contentBuffer = new StringBuilder();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(ct);
                if (line is null)
                    break;

                await WriteTraceAsync(traceWriter, "stdout", line);

                if (TryExtractContentFragment(line, out var fragment))
                {
                    contentBuffer.Append(fragment);
                    continue;
                }

                await FlushContentBufferAsync(contentBuffer, output, ct);

                if (!TryHandleWireLine(process.StandardInput, line, traceWriter, initializeId, promptId, promptCompletion, ct))
                    await output.WriteAsync(line, ct);

                foreach (var derivedLine in ExtractOutputLines(line))
                    await output.WriteAsync(derivedLine, ct);
            }

            await FlushContentBufferAsync(contentBuffer, output, ct);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
        }
    }

    private async Task ConsumeStderrAsync(
        ProcessHandle process,
        ChannelWriter<string> output,
        StreamWriter traceWriter,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(ct);
                if (line is null)
                    break;

                await WriteTraceAsync(traceWriter, "stderr", line);
                await output.WriteAsync(line, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool TryHandleWireLine(
        StreamWriter stdin,
        string line,
        StreamWriter traceWriter,
        string initializeId,
        string promptId,
        TaskCompletionSource<bool> promptCompletion,
        CancellationToken ct)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (TryBuildResponseJson(root, out var responseJson) && responseJson is not null)
            {
                _ = SendWireRequestAsync(stdin, responseJson, traceWriter, ct);
                return true;
            }

            if (!root.TryGetProperty("id", out var idProperty) || idProperty.ValueKind != JsonValueKind.String)
                return true;

            var id = idProperty.GetString();
            if (id == initializeId)
                return true;

            if (id == promptId)
            {
                promptCompletion.TrySetResult(true);
                return true;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task SendWireRequestAsync(StreamWriter stdin, string json, StreamWriter traceWriter, CancellationToken ct)
    {
        await WriteTraceAsync(traceWriter, "stdin", json);
        await stdin.WriteLineAsync(json.AsMemory(), ct);
        await stdin.FlushAsync(ct);
    }

    private static async Task FlushContentBufferAsync(StringBuilder contentBuffer, ChannelWriter<string> output, CancellationToken ct)
    {
        if (contentBuffer.Length == 0)
            return;

        var text = contentBuffer.ToString();
        contentBuffer.Clear();
        await output.WriteAsync(text, ct);
    }

    private static void TryTerminateProcess(ProcessHandle process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private ResolvedCommandContext ResolveCommandContext()
    {
        var placeholders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in _config.Defaults)
            placeholders.TryAdd(key, value);

        var effectiveModel = RuntimeFactory.MergeModel(_model, _config.DefaultModel);
        if (effectiveModel?.Provider is { } provider)
            placeholders["provider"] = provider;
        if (effectiveModel?.ModelId is { } modelId)
            placeholders["model"] = modelId;

        var resolvedPlaceholders = new Dictionary<string, string>(placeholders, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in _extraEnv)
            resolvedPlaceholders[key] = value;

        var args = _config.Args
            .Select(arg => ResolvePlaceholders(arg, resolvedPlaceholders))
            .ToArray();

        var environmentPlaceholders = new Dictionary<string, string>(_extraEnv, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in placeholders)
            environmentPlaceholders[key] = value;

        return new ResolvedCommandContext(args, placeholders, environmentPlaceholders);
    }

    private static string ResolvePlaceholders(string template, Dictionary<string, string> values)
    {
        var result = template;
        foreach (var (key, value) in values)
            result = result.Replace($"{{{key}}}", value, StringComparison.OrdinalIgnoreCase);
        return result;
    }

    private string BuildLaunchDiagnostic(ResolvedCommandContext context)
    {
        var promptHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_prompt)));
        return $"[runtime-launch] exe={_config.Executable} cwd={_workingDirectory} args=[{string.Join(", ", context.Args)}] promptLen={_prompt.Length} promptSha256={promptHash}";
    }

    private string CreateTraceFilePath()
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var safeAgentId = string.Concat(AgentId.Select(c => Array.IndexOf(invalidChars, c) >= 0 ? '_' : c));
        var safeRuntimeId = string.Concat(_config.Id.Select(c => Array.IndexOf(invalidChars, c) >= 0 ? '_' : c));
        var traceRoot = Path.Combine(Path.GetTempPath(), "giant-isopod-runtime-traces");
        Directory.CreateDirectory(traceRoot);
        return Path.Combine(
            traceRoot,
            $"{safeAgentId}-{safeRuntimeId}-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.log");
    }

    private static Task WriteTraceAsync(StreamWriter writer, string channel, string text)
    {
        return writer.WriteLineAsync($"[{DateTime.UtcNow:O}] {channel}: {text}");
    }

    private static string? GetEventKind(JsonElement eventParams)
    {
        if (eventParams.TryGetProperty("type", out var typeProperty) && typeProperty.ValueKind == JsonValueKind.String)
            return typeProperty.GetString();

        return null;
    }

    private static bool TryExtractContentText(JsonElement eventParams, out string contentText)
    {
        if (!eventParams.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
        {
            contentText = string.Empty;
            return false;
        }

        foreach (var propertyName in new[] { "text", "think" })
        {
            if (payload.TryGetProperty(propertyName, out var property))
            {
                if (property.ValueKind == JsonValueKind.String)
                {
                    contentText = property.GetString() ?? string.Empty;
                    return true;
                }

                if (property.ValueKind == JsonValueKind.Object)
                {
                    foreach (var nestedName in new[] { "text", "value" })
                    {
                        if (property.TryGetProperty(nestedName, out var nestedProperty) && nestedProperty.ValueKind == JsonValueKind.String)
                        {
                            contentText = nestedProperty.GetString() ?? string.Empty;
                            return true;
                        }
                    }
                }
            }
        }

        contentText = string.Empty;
        return false;
    }

    private static string DescribeToolName(JsonElement eventParams)
    {
        if (!eventParams.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            return "unknown";

        if (payload.TryGetProperty("function", out var functionPayload) &&
            functionPayload.ValueKind == JsonValueKind.Object &&
            functionPayload.TryGetProperty("name", out var functionName) &&
            functionName.ValueKind == JsonValueKind.String)
        {
            return functionName.GetString() ?? "unknown";
        }

        foreach (var propertyName in new[] { "tool", "toolName", "name" })
        {
            if (payload.TryGetProperty(propertyName, out var property))
            {
                if (property.ValueKind == JsonValueKind.String)
                    return property.GetString() ?? "unknown";

                if (property.ValueKind == JsonValueKind.Object)
                {
                    if (property.TryGetProperty("name", out var nestedName) && nestedName.ValueKind == JsonValueKind.String)
                        return nestedName.GetString() ?? "unknown";
                }
            }
        }

        return "unknown";
    }

    private static string DescribeToolResult(JsonElement eventParams)
    {
        if (eventParams.TryGetProperty("payload", out var payload) &&
            payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("return_value", out var returnValue) &&
            returnValue.ValueKind == JsonValueKind.Object &&
            returnValue.TryGetProperty("is_error", out var isErrorProperty) &&
            isErrorProperty.ValueKind == JsonValueKind.True)
        {
            return "error";
        }

        return "ok";
    }

    private static string DescribeStep(JsonElement eventParams)
    {
        if (!eventParams.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            return "step";

        if (payload.TryGetProperty("n", out var stepNumber) && stepNumber.ValueKind == JsonValueKind.Number)
            return $"#{stepNumber.GetInt32()}";

        foreach (var propertyName in new[] { "step", "name", "title" })
        {
            if (payload.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
                return property.GetString() ?? "step";
        }

        return "step";
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
    }

    private sealed class ResolvedCommandContext : IDisposable
    {
        public string[] Args { get; }
        public Dictionary<string, string> Placeholders { get; }
        public Dictionary<string, string> EnvironmentPlaceholders { get; }

        public ResolvedCommandContext(
            string[] args,
            Dictionary<string, string> placeholders,
            Dictionary<string, string> environmentPlaceholders)
        {
            Args = args;
            Placeholders = placeholders;
            EnvironmentPlaceholders = environmentPlaceholders;
        }

        public void Dispose()
        {
        }
    }
}
