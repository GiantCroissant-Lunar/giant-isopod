using GiantIsopod.Contracts.Core;
using GiantIsopod.Contracts.Protocol.Runtime;

namespace GiantIsopod.Plugin.Actors;

public delegate Task<RuntimeAttemptResult> RuntimeExecutionMiddlewareDelegate(
    RuntimeExecutionContext context,
    CancellationToken cancellationToken);

public interface IRuntimeExecutionMiddleware
{
    Task<RuntimeAttemptResult> InvokeAsync(
        RuntimeExecutionContext context,
        RuntimeExecutionMiddlewareDelegate next,
        CancellationToken cancellationToken);
}

public sealed class RuntimeExecutionContext
{
    public RuntimeExecutionContext(
        ExecuteTaskPrompt request,
        RuntimeConfig runtimeConfig,
        string runtimeId,
        string workingDirectory,
        int attemptNumber,
        int maxAttempts,
        string prompt)
    {
        Request = request;
        RuntimeConfig = runtimeConfig;
        RuntimeId = runtimeId;
        WorkingDirectory = workingDirectory;
        AttemptNumber = attemptNumber;
        MaxAttempts = maxAttempts;
        OriginalPrompt = prompt;
        EffectivePrompt = prompt;
    }

    public ExecuteTaskPrompt Request { get; }
    public RuntimeConfig RuntimeConfig { get; }
    public string RuntimeId { get; }
    public string WorkingDirectory { get; }
    public int AttemptNumber { get; }
    public int MaxAttempts { get; }
    public string OriginalPrompt { get; }
    public string EffectivePrompt { get; set; }
}

public static class RuntimeExecutionPipeline
{
    public static Task<RuntimeAttemptResult> ExecuteAsync(
        RuntimeExecutionContext context,
        IReadOnlyList<IRuntimeExecutionMiddleware> middlewares,
        RuntimeExecutionMiddlewareDelegate terminal,
        CancellationToken cancellationToken)
    {
        RuntimeExecutionMiddlewareDelegate current = terminal;

        for (var i = middlewares.Count - 1; i >= 0; i--)
        {
            var middleware = middlewares[i];
            var next = current;
            current = (ctx, ct) => middleware.InvokeAsync(ctx, next, ct);
        }

        return current(context, cancellationToken);
    }
}

public sealed class PromptTransportRuntimeMiddleware : IRuntimeExecutionMiddleware
{
    public Task<RuntimeAttemptResult> InvokeAsync(
        RuntimeExecutionContext context,
        RuntimeExecutionMiddlewareDelegate next,
        CancellationToken cancellationToken)
    {
        context.EffectivePrompt = context.RuntimeId switch
        {
            "gemini" => BuildGeminiPrompt(context.EffectivePrompt),
            "copilot" => BuildCopilotPrompt(context.EffectivePrompt),
            _ => context.EffectivePrompt
        };

        return next(context, cancellationToken);
    }

    private static string BuildGeminiPrompt(string prompt)
    {
        var compactPrompt = BuildCompactOneShotPrompt(prompt);
        if (!string.IsNullOrWhiteSpace(compactPrompt))
            return compactPrompt;

        return $$"""
This is a one-shot non-interactive task. Execute the task immediately in the current workspace.
Do not ask for the first task, do not ask follow-up questions, and do not wait for additional instructions.
If you cannot complete the task exactly, return a giant-isopod result envelope with outcome "failed" and a concrete failure_reason.
Return the final giant-isopod result envelope in the same response after you finish the task.

{{prompt}}
""";
    }

    private static string BuildCopilotPrompt(string prompt)
    {
        var compactPrompt = BuildCompactOneShotPrompt(prompt);
        if (!string.IsNullOrWhiteSpace(compactPrompt))
            return compactPrompt;

        return $$"""
This is a one-shot non-interactive task. The task is fully specified below.
Do not inspect unrelated files to infer a different task, do not ask what the first task is, and do not stop before attempting the requested edit.
If permissions or tools prevent completion, return a giant-isopod result envelope with outcome "failed" and a concrete failure_reason.
Return the final giant-isopod result envelope in the same response after you finish the task.

{{prompt}}
""";
    }

    private static string? BuildCompactOneShotPrompt(string prompt)
    {
        var taskId = ExtractValue(prompt, "Task ID:");
        var taskBody = ExtractSection(prompt, "Task:", "Result contract:");
        if (string.IsNullOrWhiteSpace(taskBody))
            return null;

        var ownedPaths = ExtractList(prompt, "Owned paths:", "Expected files:", "Allow no-op completion:");
        var expectedFiles = ExtractList(prompt, "Expected files:", "Allow no-op completion:", "Task:");
        var allowNoOp = ExtractValue(prompt, "Allow no-op completion:");

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(taskId))
            lines.Add($"Task ID: {taskId}");

        lines.Add("Execute the following exact task now. This is the only task.");
        lines.Add(taskBody.Trim());

        if (ownedPaths.Count > 0)
        {
            lines.Add("Owned paths:");
            lines.AddRange(ownedPaths.Select(path => $"- {path}"));
        }

        if (expectedFiles.Count > 0)
        {
            lines.Add("Expected files:");
            lines.AddRange(expectedFiles.Select(path => $"- {path}"));
        }

        if (!string.IsNullOrWhiteSpace(allowNoOp))
            lines.Add($"Allow no-op completion: {allowNoOp}");

        lines.Add("Do not inspect unrelated files, do not ask follow-up questions, and do not wait for another task.");
        lines.Add("Return the final result envelope as the last thing in your response with this exact shape:");
        lines.Add("<giant-isopod-result>{\"task_id\":\"" + (taskId ?? "string") + "\",\"outcome\":\"completed\",\"summary\":\"string\",\"no_op\":false,\"artifacts_expected\":[\"Code\"],\"failure_reason\":null,\"subplan\":null}</giant-isopod-result>");
        lines.Add("If you cannot complete the task, return outcome=\"failed\" with failure_reason instead of asking for more input.");

        return string.Join(Environment.NewLine, lines);
    }

    private static string? ExtractValue(string prompt, string prefix)
    {
        foreach (var rawLine in prompt.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            if (rawLine.StartsWith(prefix, StringComparison.Ordinal))
                return rawLine[prefix.Length..].Trim();
        }

        return null;
    }

    private static string ExtractSection(string prompt, string startMarker, params string[] endMarkers)
    {
        var startIndex = prompt.IndexOf(startMarker, StringComparison.Ordinal);
        if (startIndex < 0)
            return string.Empty;

        startIndex += startMarker.Length;
        var endIndex = prompt.Length;
        foreach (var endMarker in endMarkers.Where(marker => !string.IsNullOrWhiteSpace(marker)))
        {
            var candidate = prompt.IndexOf(endMarker, startIndex, StringComparison.Ordinal);
            if (candidate >= 0 && candidate < endIndex)
                endIndex = candidate;
        }

        return prompt[startIndex..endIndex].Trim();
    }

    private static IReadOnlyList<string> ExtractList(string prompt, string startMarker, params string[] endMarkers)
    {
        var section = ExtractSection(prompt, startMarker, endMarkers);
        if (string.IsNullOrWhiteSpace(section))
            return [];

        var lines = section
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("- ", StringComparison.Ordinal))
            .Select(line => line[2..].Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        return lines;
    }
}

public sealed class StructuredResultNormalizationMiddleware : IRuntimeExecutionMiddleware
{
    public Task<RuntimeAttemptResult> InvokeAsync(
        RuntimeExecutionContext context,
        RuntimeExecutionMiddlewareDelegate next,
        CancellationToken cancellationToken)
    {
        return NormalizeAsync(context, next, cancellationToken);
    }

    private static async Task<RuntimeAttemptResult> NormalizeAsync(
        RuntimeExecutionContext context,
        RuntimeExecutionMiddlewareDelegate next,
        CancellationToken cancellationToken)
    {
        var result = await next(context, cancellationToken);
        if (result.Parsed.HasEnvelope)
            return result;

        if (ContainsInteractiveHandshake(result.Transcript))
        {
            return result with
            {
                Parsed = BuildFailure(
                    context.Request.TaskId,
                    "Runtime requested follow-up input instead of executing the assigned task."),
                Retryable = true,
                RetryReason = "Runtime asked for more instructions instead of executing the current task."
            };
        }

        if (ContainsPermissionDenied(result.Transcript))
        {
            return result with
            {
                Parsed = BuildFailure(
                    context.Request.TaskId,
                    "Runtime could not use the required tools or file permissions to complete the task."),
                Retryable = false,
                RetryReason = null
            };
        }

        if (result.Artifacts.Count > 0)
        {
            return result with
            {
                Parsed = new StructuredTaskResultParser.ParsedTaskResult(
                    HasEnvelope: true,
                    EnvelopeTaskId: context.Request.TaskId,
                    Outcome: StructuredTaskResultParser.ParsedTaskOutcome.Completed,
                    Summary: BuildArtifactSummary(result.Artifacts),
                    FailureReason: null,
                    NoOp: false,
                    Subplan: null,
                    ExpectedArtifactTypes: Array.Empty<ArtifactType>()),
                Retryable = false,
                RetryReason = null
            };
        }

        return result;
    }

    private static bool ContainsInteractiveHandshake(string transcript)
    {
        return transcript.Contains("What's your first task?", StringComparison.OrdinalIgnoreCase) ||
               transcript.Contains("What’s your first task?", StringComparison.OrdinalIgnoreCase) ||
               transcript.Contains("I'm ready to assist", StringComparison.OrdinalIgnoreCase) ||
               transcript.Contains("I’m ready to assist", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsPermissionDenied(string transcript)
    {
        return transcript.Contains("Permission denied and could not request permission from user", StringComparison.OrdinalIgnoreCase) ||
               transcript.Contains("permission denied", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildArtifactSummary(IReadOnlyList<ArtifactRef> artifacts)
    {
        var changedFiles = artifacts
            .Select(a => a.Metadata != null && a.Metadata.TryGetValue("relativePath", out var path) ? path : a.Uri)
            .Take(5)
            .ToArray();

        var suffix = artifacts.Count > changedFiles.Length ? "..." : string.Empty;
        return $"Runtime completed without a structured envelope, but produced {artifacts.Count} artifact(s): {string.Join(", ", changedFiles)}{suffix}";
    }

    private static StructuredTaskResultParser.ParsedTaskResult BuildFailure(string taskId, string reason)
    {
        return new StructuredTaskResultParser.ParsedTaskResult(
            HasEnvelope: true,
            EnvelopeTaskId: taskId,
            Outcome: StructuredTaskResultParser.ParsedTaskOutcome.Failed,
            Summary: reason,
            FailureReason: reason,
            NoOp: false,
            Subplan: null,
            ExpectedArtifactTypes: Array.Empty<ArtifactType>());
    }
}

public sealed class RetryTimeoutRuntimeMiddleware : IRuntimeExecutionMiddleware
{
    public async Task<RuntimeAttemptResult> InvokeAsync(
        RuntimeExecutionContext context,
        RuntimeExecutionMiddlewareDelegate next,
        CancellationToken cancellationToken)
    {
        var timeout = GetAttemptTimeout(context.RuntimeId);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            var result = await next(context, timeoutCts.Token);
            if (result.Retryable && context.AttemptNumber < context.MaxAttempts)
                return result;

            if (!result.Parsed.HasEnvelope && context.AttemptNumber < context.MaxAttempts)
            {
                return result with
                {
                    Retryable = true,
                    RetryReason = "Runtime did not return a structured result envelope."
                };
            }

            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            var timeoutMessage = $"Runtime attempt timed out after {timeout.TotalSeconds:0} seconds.";
            return new RuntimeAttemptResult(
                Parsed: new StructuredTaskResultParser.ParsedTaskResult(
                    HasEnvelope: true,
                    EnvelopeTaskId: context.Request.TaskId,
                    Outcome: StructuredTaskResultParser.ParsedTaskOutcome.Failed,
                    Summary: timeoutMessage,
                    FailureReason: timeoutMessage,
                    NoOp: false,
                    Subplan: null,
                    ExpectedArtifactTypes: Array.Empty<ArtifactType>()),
                Artifacts: Array.Empty<ArtifactRef>(),
                Transcript: string.Empty,
                Retryable: context.AttemptNumber < context.MaxAttempts,
                RetryReason: timeoutMessage);
        }
    }

    private static TimeSpan GetAttemptTimeout(string runtimeId)
    {
        return runtimeId switch
        {
            "copilot" => TimeSpan.FromSeconds(45),
            "gemini" => TimeSpan.FromSeconds(45),
            _ => TimeSpan.FromMinutes(2)
        };
    }
}
