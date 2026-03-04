using Akka.Actor;
using CliWrap;
using CliWrap.Buffered;
using GiantIsopod.Contracts.Core;
using GiantIsopod.Plugin.Process;
using Microsoft.Extensions.Logging;
using System.Text;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// /user/validator — runs pluggable validators against task artifacts (ADR-011).
/// Supports Script validators (CLI exit-code gate) and AgentReview (placeholder).
/// </summary>
public sealed class ValidatorActor : UntypedActor
{
    private readonly IActorRef _artifactRegistry;
    private readonly AgentWorldConfig? _config;
    private readonly ILogger<ValidatorActor> _logger;

    private readonly Dictionary<ArtifactType, List<ValidatorSpec>> _validators = new();
    private readonly Dictionary<string, PendingValidation> _pending = new();

    internal static readonly TimeSpan ScriptTimeout = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan AgentReviewTimeout = TimeSpan.FromMinutes(5);
    private const int MaxEmbeddedArtifactChars = 12000;

    public ValidatorActor(IActorRef artifactRegistry, ILogger<ValidatorActor> logger)
        : this(artifactRegistry, config: null, logger)
    {
    }

    public ValidatorActor(IActorRef artifactRegistry, AgentWorldConfig? config, ILogger<ValidatorActor> logger)
    {
        _artifactRegistry = artifactRegistry;
        _config = config;
        _logger = logger;
    }

    protected override void OnReceive(object message)
    {
        switch (message)
        {
            case RegisterValidator register:
                HandleRegister(register);
                break;

            case ValidateArtifact validate:
                HandleValidate(validate);
                break;

            case ScriptResult result:
                HandleScriptResult(result);
                break;

            case AgentReviewResult result:
                HandleScriptResult(new ScriptResult(result.ArtifactId, result.ValidatorName, result.Passed, result.Details));
                break;
        }
    }

    private void HandleRegister(RegisterValidator msg)
    {
        var spec = msg.Spec;
        if (!_validators.TryGetValue(spec.AppliesTo, out var list))
        {
            list = new List<ValidatorSpec>();
            _validators[spec.AppliesTo] = list;
        }

        // Replace existing validator with same name
        list.RemoveAll(v => v.Name == spec.Name);
        list.Add(spec);

        _logger.LogInformation("Validator registered: {Name} ({Kind}) for {Type}",
            spec.Name, spec.Kind, spec.AppliesTo);
        Sender.Tell(new ValidatorRegistered(spec.Name));
    }

    private void HandleValidate(ValidateArtifact msg)
    {
        var sender = Sender;
        var scopeFailure = ValidateArtifactScope(msg);
        if (scopeFailure is not null)
        {
            _artifactRegistry.Tell(new UpdateValidation(msg.ArtifactId, scopeFailure));
            sender.Tell(new ValidationComplete(msg.ArtifactId, new[] { scopeFailure }, msg.TaskId));
            return;
        }

        var applicable = GetApplicableValidators(msg.Artifact.Type, msg.RequiredValidators);

        if (applicable.Count == 0)
        {
            // If the caller explicitly requested validators by name but none matched,
            // report each missing validator as a failure rather than silently auto-passing.
            if (msg.RequiredValidators is { Count: > 0 })
            {
                var failures = msg.RequiredValidators
                    .Select(name => new ValidatorResult(name, false, "Required validator not registered"))
                    .ToArray();
                sender.Tell(new ValidationComplete(msg.ArtifactId, failures, msg.TaskId));
                return;
            }

            sender.Tell(new ValidationComplete(msg.ArtifactId, Array.Empty<ValidatorResult>(), msg.TaskId));
            return;
        }

        if (_pending.ContainsKey(msg.ArtifactId))
        {
            _logger.LogWarning("Validation already in-flight for artifact {ArtifactId}, ignoring duplicate", msg.ArtifactId);
            return;
        }

        var pending = new PendingValidation(sender, applicable.Count, new List<ValidatorResult>(), msg.TaskId);
        _pending[msg.ArtifactId] = pending;

        foreach (var spec in applicable)
        {
            switch (spec.Kind)
            {
                case ValidatorKind.Script:
                    RunScriptValidator(msg.ArtifactId, msg.Artifact, spec);
                    break;

                case ValidatorKind.AgentReview:
                    RunAgentReviewValidator(
                        msg.ArtifactId,
                        msg.Artifact,
                        msg.TaskId,
                        msg.TaskDescription,
                        spec,
                        msg.OwnedPaths,
                        msg.ExpectedFiles);
                    break;
            }
        }
    }

    private void RunScriptValidator(string artifactId, ArtifactRef artifact, ValidatorSpec spec)
    {
        var self = Self;

        RunCommandAsync(spec.Command, artifact.Uri)
            .ContinueWith(t =>
            {
                if (t.IsCanceled)
                {
                    return (object)new ScriptResult(artifactId, spec.Name, Passed: false,
                        Details: "Script timed out or was cancelled");
                }

                if (t.IsFaulted)
                {
                    return (object)new ScriptResult(artifactId, spec.Name, Passed: false,
                        Details: $"Script exception: {t.Exception?.GetBaseException().Message}");
                }

                var (exitCode, stderr) = t.Result;
                return new ScriptResult(artifactId, spec.Name,
                    Passed: exitCode == 0,
                    Details: exitCode == 0 ? null : $"Exit code {exitCode}: {stderr}");
            })
            .PipeTo(self);
    }

    private void RunAgentReviewValidator(
        string artifactId,
        ArtifactRef artifact,
        string? taskId,
        string? taskDescription,
        ValidatorSpec spec,
        IReadOnlyList<string>? ownedPaths,
        IReadOnlyList<string>? expectedFiles)
    {
        var self = Self;

        RunAgentReviewAsync(artifactId, artifact, taskId, taskDescription, spec, ownedPaths, expectedFiles)
            .ContinueWith(t =>
            {
                if (t.IsCanceled)
                {
                    return (object)new AgentReviewResult(
                        artifactId, spec.Name, Passed: false,
                        Details: "Agent review timed out or was cancelled");
                }

                if (t.IsFaulted)
                {
                    return (object)new AgentReviewResult(
                        artifactId, spec.Name, Passed: false,
                        Details: $"Agent review exception: {t.Exception?.GetBaseException().Message}");
                }

                return t.Result;
            })
            .PipeTo(self);
    }

    private void HandleScriptResult(ScriptResult result)
    {
        if (!_pending.TryGetValue(result.ArtifactId, out var pending))
        {
            _logger.LogWarning("Received script result for unknown artifact {ArtifactId}", result.ArtifactId);
            return;
        }

        var validatorResult = new ValidatorResult(result.ValidatorName, result.Passed, result.Details);
        pending.Results.Add(validatorResult);

        _logger.LogDebug("Validator '{Name}' for artifact {ArtifactId}: {Status}",
            result.ValidatorName, result.ArtifactId, result.Passed ? "PASS" : "FAIL");

        if (pending.Results.Count < pending.Expected)
            return;

        // All validators completed for this artifact
        _pending.Remove(result.ArtifactId);

        // Forward each result to artifact registry
        foreach (var vr in pending.Results)
            _artifactRegistry.Tell(new UpdateValidation(result.ArtifactId, vr));

        pending.Requester.Tell(new ValidationComplete(result.ArtifactId, pending.Results, pending.TaskId));
    }

    private List<ValidatorSpec> GetApplicableValidators(ArtifactType type, IReadOnlyList<string>? requiredValidators)
    {
        if (!_validators.TryGetValue(type, out var all))
            return new List<ValidatorSpec>();

        if (requiredValidators is null or { Count: 0 })
            return new List<ValidatorSpec>(all);

        var required = new HashSet<string>(requiredValidators);
        return all.Where(v => required.Contains(v.Name)).ToList();
    }

    private static async Task<(int ExitCode, string Stderr)> RunCommandAsync(string command, string artifactUri)
    {
        using var cts = new CancellationTokenSource(ScriptTimeout);

        // Split command into tokens; artifactUri is appended as a distinct element so CliWrap
        // escapes it properly (handles spaces and shell metacharacters without injection risk).
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var exe = parts[0];
        var argTokens = parts.Length > 1
            ? [..parts[1..], artifactUri]
            : (string[])[artifactUri];

        var result = await Cli.Wrap(exe)
            .WithArguments(argTokens)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cts.Token);

        return (result.ExitCode, result.StandardError);
    }

    private async Task<AgentReviewResult> RunAgentReviewAsync(
        string artifactId,
        ArtifactRef artifact,
        string? taskId,
        string? taskDescription,
        ValidatorSpec spec,
        IReadOnlyList<string>? ownedPaths,
        IReadOnlyList<string>? expectedFiles)
    {
        if (_config is null)
        {
            return new AgentReviewResult(
                artifactId, spec.Name, Passed: false,
                Details: "Agent review runtime is not configured.");
        }

        using var cts = new CancellationTokenSource(AgentReviewTimeout);

        var runtimeId = ResolveReviewRuntimeId(spec);
        var runtimeConfig = _config.Runtimes.ResolveOrDefault(runtimeId);
        var model = ResolveReviewModel(spec);
        var workingDirectory = ResolveReviewWorkingDirectory(artifact);
        var prompt = BuildAgentReviewPrompt(artifactId, artifact, taskId, taskDescription, spec, ownedPaths, expectedFiles);

        var output = new StringBuilder();
        await using var runtime = RuntimeFactory.Create(
            agentId: $"validator-{spec.Name}",
            config: runtimeConfig,
            model: model,
            workingDirectory: workingDirectory,
            extraEnv: _config.RuntimeEnvironment);

        await runtime.StartAsync(cts.Token);
        await runtime.SendAsync(prompt, cts.Token);

        await foreach (var line in runtime.ReadEventsAsync(cts.Token))
        {
            if (!string.IsNullOrWhiteSpace(line))
                output.AppendLine(line);
        }

        var parsed = StructuredReviewResultParser.Parse(output.ToString(), spec.Name, artifactId);
        if (!parsed.HasEnvelope)
        {
            return new AgentReviewResult(
                artifactId, spec.Name, Passed: false,
                Details: "Agent review did not return the required structured review envelope.");
        }

        if (!string.IsNullOrWhiteSpace(parsed.ValidatorName) &&
            !string.Equals(parsed.ValidatorName, spec.Name, StringComparison.Ordinal))
        {
            return new AgentReviewResult(
                artifactId, spec.Name, Passed: false,
                Details: $"Agent review returned unexpected validator '{parsed.ValidatorName}'.");
        }

        if (!string.IsNullOrWhiteSpace(parsed.ArtifactId) &&
            !string.Equals(parsed.ArtifactId, artifactId, StringComparison.Ordinal))
        {
            return new AgentReviewResult(
                artifactId, spec.Name, Passed: false,
                Details: $"Agent review returned unexpected artifact '{parsed.ArtifactId}'.");
        }

        return new AgentReviewResult(
            artifactId,
            spec.Name,
            parsed.Passed,
            BuildReviewDetails(parsed));
    }

    private string ResolveReviewRuntimeId(ValidatorSpec spec)
    {
        if (spec.Config is not null &&
            spec.Config.TryGetValue("runtimeId", out var configuredRuntimeId) &&
            !string.IsNullOrWhiteSpace(configuredRuntimeId))
        {
            return configuredRuntimeId;
        }

        if (!string.IsNullOrWhiteSpace(spec.Command))
            return spec.Command;

        return _config?.DefaultRuntimeId
            ?? throw new InvalidOperationException("No runtime configured for agent review validator.");
    }

    private static ModelSpec? ResolveReviewModel(ValidatorSpec spec)
    {
        if (spec.Config is null)
            return null;

        spec.Config.TryGetValue("provider", out var provider);
        spec.Config.TryGetValue("model", out var modelId);
        if (string.IsNullOrWhiteSpace(provider) && string.IsNullOrWhiteSpace(modelId))
            return null;

        return new ModelSpec(
            Provider: string.IsNullOrWhiteSpace(provider) ? null : provider,
            ModelId: string.IsNullOrWhiteSpace(modelId) ? null : modelId);
    }

    private string ResolveReviewWorkingDirectory(ArtifactRef artifact)
    {
        if (Uri.TryCreate(artifact.Uri, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            var path = uri.LocalPath;
            var dir = File.Exists(path) ? Path.GetDirectoryName(path) : null;
            if (!string.IsNullOrWhiteSpace(dir))
                return dir;
        }

        if (File.Exists(artifact.Uri))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(artifact.Uri));
            if (!string.IsNullOrWhiteSpace(dir))
                return dir;
        }

        if (!string.IsNullOrWhiteSpace(_config?.RuntimeWorkingDirectory))
            return _config.RuntimeWorkingDirectory;

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private string BuildAgentReviewPrompt(
        string artifactId,
        ArtifactRef artifact,
        string? taskId,
        string? taskDescription,
        ValidatorSpec spec,
        IReadOnlyList<string>? ownedPaths,
        IReadOnlyList<string>? expectedFiles)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are validating a generated artifact.");
        sb.Append("Validator: ").AppendLine(spec.Name);
        sb.Append("Artifact ID: ").AppendLine(artifactId);
        if (!string.IsNullOrWhiteSpace(taskId))
            sb.Append("Task ID: ").AppendLine(taskId);
        if (!string.IsNullOrWhiteSpace(taskDescription))
        {
            sb.AppendLine("Current task description:");
            sb.AppendLine(taskDescription);
        }
        sb.Append("Artifact type: ").AppendLine(artifact.Type.ToString());
        sb.Append("Artifact format: ").AppendLine(artifact.Format);
        sb.Append("Artifact URI: ").AppendLine(artifact.Uri);
        if (ownedPaths is { Count: > 0 })
        {
            sb.AppendLine("Owned paths:");
            foreach (var path in ownedPaths)
                sb.Append("- ").AppendLine(path);
        }
        if (expectedFiles is { Count: > 0 })
        {
            sb.AppendLine("Expected files:");
            foreach (var file in expectedFiles)
                sb.Append("- ").AppendLine(file);
        }
        if (!string.IsNullOrWhiteSpace(spec.Rubric))
        {
            sb.AppendLine();
            sb.AppendLine("Rubric:");
            sb.AppendLine(spec.Rubric);
        }

        if (!string.IsNullOrWhiteSpace(taskDescription))
        {
            sb.AppendLine();
            sb.AppendLine("Judge the artifact against the current task description above, even if the validator rubric contains broader parent-task context.");
        }

        if (artifact.Metadata is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("Artifact metadata:");
            foreach (var (key, value) in artifact.Metadata)
                sb.Append("- ").Append(key).Append(": ").AppendLine(value);
        }

        var embeddedContent = TryReadArtifactContent(artifact);
        if (!string.IsNullOrWhiteSpace(embeddedContent))
        {
            sb.AppendLine();
            sb.AppendLine("Artifact content excerpt:");
            sb.AppendLine(embeddedContent);
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("Use your file-reading tools if you need to inspect the artifact contents directly.");
        }

        sb.AppendLine();
        sb.AppendLine("Return a final machine-readable review envelope as the last thing in your response.");
        sb.AppendLine("Do not wrap it in markdown fences.");
        sb.AppendLine("The envelope must exactly match the validator and artifact id below.");
        sb.AppendLine("If the artifact passes review, set passed=true and issues=[].");
        sb.AppendLine("If it fails, set passed=false and include actionable issues.");
        sb.AppendLine("Schema:");
        sb.AppendLine("{\"validator\":\"string\",\"artifact_id\":\"string\",\"passed\":true,\"summary\":\"string\",\"issues\":[]}");
        sb.AppendLine("{\"validator\":\"string\",\"artifact_id\":\"string\",\"passed\":false,\"summary\":\"string\",\"issues\":[\"string\"]}");
        sb.AppendLine("Example:");
        sb.AppendLine("<giant-isopod-review>");
        sb.AppendLine("{\"validator\":\"validator-name\",\"artifact_id\":\"artifact-id\",\"passed\":true,\"summary\":\"Looks good.\",\"issues\":[]}");
        sb.AppendLine("</giant-isopod-review>");
        return sb.ToString();
    }

    private static ValidatorResult? ValidateArtifactScope(ValidateArtifact msg)
    {
        var relativePath = GetArtifactRelativePath(msg.Artifact);
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        if (msg.ExpectedFiles is { Count: > 0 } &&
            !msg.ExpectedFiles.Any(expected => PathsMatch(expected, relativePath)))
        {
            return new ValidatorResult(
                "expected-files",
                false,
                $"Artifact path '{relativePath}' is outside expected_files [{string.Join(", ", msg.ExpectedFiles)}].");
        }

        if (msg.OwnedPaths is { Count: > 0 } &&
            !msg.OwnedPaths.Any(owned => PathWithinOwnedPath(relativePath, owned)))
        {
            return new ValidatorResult(
                "owned-paths",
                false,
                $"Artifact path '{relativePath}' is outside owned_paths [{string.Join(", ", msg.OwnedPaths)}].");
        }

        return null;
    }

    private static string? GetArtifactRelativePath(ArtifactRef artifact)
    {
        if (artifact.Metadata is null ||
            !artifact.Metadata.TryGetValue("relativePath", out var relativePath) ||
            string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        return NormalizePath(relativePath);
    }

    private static bool PathsMatch(string expectedPath, string relativePath)
    {
        return string.Equals(NormalizePath(expectedPath), NormalizePath(relativePath), StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathWithinOwnedPath(string relativePath, string ownedPath)
    {
        var normalizedOwnedPath = NormalizePath(ownedPath);
        var normalizedRelativePath = NormalizePath(relativePath);
        if (string.IsNullOrWhiteSpace(normalizedOwnedPath) || string.IsNullOrWhiteSpace(normalizedRelativePath))
            return false;

        return string.Equals(normalizedOwnedPath, normalizedRelativePath, StringComparison.OrdinalIgnoreCase)
               || normalizedRelativePath.StartsWith(normalizedOwnedPath + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        var normalized = path.Trim().Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];

        return normalized.Trim('/');
    }

    private static string? TryReadArtifactContent(ArtifactRef artifact)
    {
        var path = ResolveArtifactPath(artifact.Uri);
        if (path is null || !File.Exists(path) || !LooksLikeTextArtifact(artifact, path))
            return null;

        var fileInfo = new FileInfo(path);
        if (fileInfo.Length == 0)
            return string.Empty;

        // For small files (conservative 4-byte-per-char UTF-8 estimate), use simple read.
        if (fileInfo.Length <= MaxEmbeddedArtifactChars * 4L)
        {
            var smallContent = File.ReadAllText(path);
            if (smallContent.Length <= MaxEmbeddedArtifactChars)
                return smallContent;

            return $"{smallContent[..MaxEmbeddedArtifactChars]}\n...[truncated]";
        }

        // For larger files, stream only the first MaxEmbeddedArtifactChars characters.
        using var fs = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);

        using var reader = new StreamReader(
            fs,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: false);

        var buffer = new char[MaxEmbeddedArtifactChars];
        var totalRead = reader.ReadBlock(buffer, 0, MaxEmbeddedArtifactChars);
        if (totalRead == 0)
            return string.Empty;

        var prefix = new string(buffer, 0, totalRead);
        var isTruncated = reader.Read() != -1;

        return isTruncated
            ? $"{prefix}\n...[truncated]"
            : prefix;
    }

    private static string? ResolveArtifactPath(string uriOrPath)
    {
        if (Uri.TryCreate(uriOrPath, UriKind.Absolute, out var uri) && uri.IsFile)
            return uri.LocalPath;

        return Path.IsPathRooted(uriOrPath) ? uriOrPath : null;
    }

    private static bool LooksLikeTextArtifact(ArtifactRef artifact, string path)
    {
        if (artifact.Format.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            return true;

        return Path.GetExtension(path).ToLowerInvariant() is
            ".cs" or ".txt" or ".md" or ".json" or ".xml" or ".yml" or ".yaml" or ".toml" or
            ".js" or ".ts" or ".tsx" or ".jsx" or ".html" or ".css" or ".csproj" or ".sln" or
            ".config" or ".ini" or ".props" or ".targets";
    }

    private static string BuildReviewDetails(StructuredReviewResultParser.ParsedReviewResult parsed)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(parsed.Summary))
            parts.Add(parsed.Summary);

        if (parsed.Issues.Count > 0)
            parts.Add($"Issues: {string.Join("; ", parsed.Issues)}");

        return parts.Count == 0
            ? (parsed.Passed ? "Agent review passed." : "Agent review failed.")
            : string.Join(" ", parts);
    }

    // ── Internal types ──

    internal sealed record ScriptResult(string ArtifactId, string ValidatorName, bool Passed, string? Details);
    internal sealed record AgentReviewResult(string ArtifactId, string ValidatorName, bool Passed, string? Details);

    private sealed record PendingValidation(
        IActorRef Requester, int Expected, List<ValidatorResult> Results, string? TaskId);
}
