using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
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
        var commandContext = ResolveCommandContext();
        var traceFilePath = CreateTraceFilePath();
        using var traceWriter = new StreamWriter(traceFilePath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        try
        {
            await WriteTraceAsync(traceWriter, "meta", $"agent={AgentId} runtime={_config.Id} cwd={_workingDirectory}");
            yield return $"[runtime-trace] file={traceFilePath}";
            yield return BuildLaunchDiagnostic(commandContext);

            var cmd = Cli.Wrap(_config.Executable)
                .WithArguments(commandContext.Args)
                .WithWorkingDirectory(_workingDirectory)
                .WithEnvironmentVariables(env =>
                {
                    foreach (var (key, value) in _config.Env)
                        env.Set(key, ResolvePlaceholders(value, commandContext.EnvironmentPlaceholders));
                })
                .WithValidation(CommandResultValidation.None);

            IsRunning = true;

            await foreach (var cmdEvent in cmd.ListenAsync(linkedCts.Token))
            {
                switch (cmdEvent)
                {
                    case StandardOutputCommandEvent stdOut:
                        if (!string.IsNullOrEmpty(stdOut.Text))
                        {
                            await WriteTraceAsync(traceWriter, "stdout", stdOut.Text);
                            yield return stdOut.Text;
                        }
                        break;
                    case StandardErrorCommandEvent stdErr:
                        if (!string.IsNullOrEmpty(stdErr.Text))
                        {
                            await WriteTraceAsync(traceWriter, "stderr", stdErr.Text);
                            yield return stdErr.Text;
                        }
                        break;
                }
            }
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

    private ResolvedCommandContext ResolveCommandContext()
    {
        var placeholders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["prompt"] = _prompt
        };

        string? promptFilePath = null;
        if (NeedsPromptFile())
        {
            promptFilePath = CreatePromptFile();
            placeholders["prompt_file_path"] = promptFilePath;
            placeholders["prompt_file_ref"] = $"@{promptFilePath}";
        }

        // Merge defaults from config
        foreach (var (key, value) in _config.Defaults)
            placeholders.TryAdd(key, value);

        // Override with explicit model spec if provided
        var effectiveModel = RuntimeFactory.MergeModel(_model, _config.DefaultModel);
        if (effectiveModel?.Provider is { } provider)
            placeholders["provider"] = provider;
        if (effectiveModel?.ModelId is { } modelId)
            placeholders["model"] = modelId;

        var args = _config.Args
            .Select(arg => ResolvePlaceholders(arg, placeholders))
            .ToArray();

        var environmentPlaceholders = new Dictionary<string, string>(_extraEnv, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in placeholders)
            environmentPlaceholders[key] = value;

        return new ResolvedCommandContext(args, placeholders, environmentPlaceholders, promptFilePath);
    }

    private bool NeedsPromptFile()
    {
        if (_config.Args.Any(arg => arg.Contains("{prompt_file_", StringComparison.OrdinalIgnoreCase)))
            return true;

        return _config.Env.Values.Any(value => value.Contains("{prompt_file_", StringComparison.OrdinalIgnoreCase));
    }

    private string CreatePromptFile()
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var safeAgentId = string.Concat(AgentId.Select(c => Array.IndexOf(invalidChars, c) >= 0 ? '_' : c));
        var fileName = $"giant-isopod-prompt-{safeAgentId}-{Guid.NewGuid():N}.md";
        var path = Path.Combine(Path.GetTempPath(), fileName);
        File.WriteAllText(path, _prompt, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
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

        var promptPlaceholder = $"<prompt len={_prompt.Length} sha256={promptHash[..12]}>";
        var promptFilePlaceholder = context.PromptFilePath is null
            ? null
            : $"<prompt-file path={context.PromptFilePath}>";

        var sanitizedArgs = context.Args
            .Select(arg =>
            {
                if (string.Equals(arg, _prompt, StringComparison.Ordinal))
                    return promptPlaceholder;

                var sanitized = arg.Replace(_prompt, promptPlaceholder, StringComparison.Ordinal);
                if (!string.IsNullOrWhiteSpace(context.PromptFilePath) && promptFilePlaceholder is not null)
                    sanitized = sanitized.Replace(context.PromptFilePath, promptFilePlaceholder, StringComparison.Ordinal);

                return sanitized;
            })
            .ToArray();

        return $"[runtime-launch] exe={_config.Executable} cwd={_workingDirectory} args=[{string.Join(", ", sanitizedArgs)}] promptLen={_prompt.Length} promptSha256={promptHash} promptFile={(context.PromptFilePath is null ? "<none>" : promptFilePlaceholder)}";
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
        public string? PromptFilePath { get; }

        public ResolvedCommandContext(
            string[] args,
            Dictionary<string, string> placeholders,
            Dictionary<string, string> environmentPlaceholders,
            string? promptFilePath)
        {
            Args = args;
            Placeholders = placeholders;
            EnvironmentPlaceholders = environmentPlaceholders;
            PromptFilePath = promptFilePath;
        }

        public void Dispose()
        {
            if (string.IsNullOrWhiteSpace(PromptFilePath))
                return;

            try
            {
                if (File.Exists(PromptFilePath))
                    File.Delete(PromptFilePath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
