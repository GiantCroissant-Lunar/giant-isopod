using GiantIsopod.Contracts.Core;
using GiantIsopod.Contracts.Protocol.Runtime;
using GiantIsopod.Plugin.Actors;
using Xunit;

namespace GiantIsopod.Plugin.Actors.Tests;

public class RuntimeExecutionMiddlewareTests
{
    [Fact]
    public async Task PromptTransportMiddleware_HardensGeminiPrompt()
    {
        var middleware = new PromptTransportRuntimeMiddleware();
        var context = CreateContext(runtimeId: "gemini", prompt: "Create Probe.txt.");

        RuntimeExecutionContext? observed = null;
        await middleware.InvokeAsync(
            context,
            (ctx, _) =>
            {
                observed = ctx;
                return Task.FromResult(CreateUnknownResult());
            },
            CancellationToken.None);

        Assert.NotNull(observed);
        Assert.Contains("Do not ask for the first task", observed!.EffectivePrompt, StringComparison.Ordinal);
        Assert.Contains("Create Probe.txt.", observed.EffectivePrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StructuredResultNormalizationMiddleware_NormalizesInteractiveHandshakeAsRetryableFailure()
    {
        var middleware = new StructuredResultNormalizationMiddleware();
        var context = CreateContext(runtimeId: "gemini");

        var result = await middleware.InvokeAsync(
            context,
            (_, _) => Task.FromResult(new RuntimeAttemptResult(
                Parsed: UnknownParsed(),
                Artifacts: Array.Empty<ArtifactRef>(),
                Transcript: "Confirmed. I'm ready to assist. What's your first task?")),
            CancellationToken.None);

        Assert.True(result.Parsed.HasEnvelope);
        Assert.Equal(StructuredTaskResultParser.ParsedTaskOutcome.Failed, result.Parsed.Outcome);
        Assert.False(result.Retryable);
        Assert.Contains("follow-up input", result.Parsed.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StructuredResultNormalizationMiddleware_SynthesizesCompletionWhenArtifactsExist()
    {
        var middleware = new StructuredResultNormalizationMiddleware();
        var context = CreateContext(runtimeId: "copilot");
        var artifact = new ArtifactRef(
            ArtifactId: "artifact-1",
            Type: ArtifactType.Code,
            Format: "text/plain",
            Uri: "file:///Probe.txt",
            ContentHash: null,
            Provenance: new ArtifactProvenance("task-1", "agent-1", DateTimeOffset.UtcNow),
            Metadata: new Dictionary<string, string> { ["relativePath"] = "Probe.txt" });

        var result = await middleware.InvokeAsync(
            context,
            (_, _) => Task.FromResult(new RuntimeAttemptResult(
                Parsed: UnknownParsed(),
                Artifacts: new[] { artifact },
                Transcript: "Created Probe.txt.")),
            CancellationToken.None);

        Assert.True(result.Parsed.HasEnvelope);
        Assert.Equal(StructuredTaskResultParser.ParsedTaskOutcome.Completed, result.Parsed.Outcome);
        Assert.Contains("Probe.txt", result.Parsed.Summary, StringComparison.Ordinal);
        Assert.False(result.Retryable);
    }

    [Fact]
    public async Task RetryTimeoutRuntimeMiddleware_ConvertsTimeoutToRetryableFailure()
    {
        var middleware = new RetryTimeoutRuntimeMiddleware();
        var context = CreateContext(runtimeId: "gemini", attemptNumber: 1, maxAttempts: 3);

        var result = await middleware.InvokeAsync(
            context,
            async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(130), ct);
                return CreateUnknownResult();
            },
            CancellationToken.None);

        Assert.True(result.Parsed.HasEnvelope);
        Assert.Equal(StructuredTaskResultParser.ParsedTaskOutcome.Failed, result.Parsed.Outcome);
        Assert.False(result.Retryable);
        Assert.Contains("timed out", result.Parsed.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplyRetryHeuristics_MarksMissingArtifactsAsRetryable()
    {
        var request = new ExecuteTaskPrompt("agent-1", "task-1", "Do the task.", AllowNoOpCompletion: false);
        var attempt = new RuntimeAttemptResult(
            Parsed: CompletedParsed(expectedArtifacts: [ArtifactType.Doc]),
            Artifacts: Array.Empty<ArtifactRef>(),
            Transcript: "claimed success");

        var result = AgentRuntimeActor.ApplyRetryHeuristics(request, attempt);

        Assert.True(result.Retryable);
        Assert.Contains("required workspace changes", result.RetryReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplyRetryHeuristics_AddsNoOpHintForNoOpCapableTasks()
    {
        var request = new ExecuteTaskPrompt("agent-1", "task-1", "Do the task.", AllowNoOpCompletion: true);
        var attempt = new RuntimeAttemptResult(
            Parsed: CompletedParsed(expectedArtifacts: Array.Empty<ArtifactType>()),
            Artifacts: Array.Empty<ArtifactRef>(),
            Transcript: "claimed success");

        var result = AgentRuntimeActor.ApplyRetryHeuristics(request, attempt);

        Assert.True(result.Retryable);
        Assert.Contains("no_op=true", result.RetryReason, StringComparison.Ordinal);
    }

    private static RuntimeExecutionContext CreateContext(
        string runtimeId,
        string prompt = "Do the task.",
        int attemptNumber = 1,
        int maxAttempts = 3)
    {
        return new RuntimeExecutionContext(
            new ExecuteTaskPrompt("agent-1", "task-1", prompt, GraphId: "graph-1"),
            new CliRuntimeConfig
            {
                Id = runtimeId,
                DisplayName = runtimeId,
                Executable = runtimeId,
                Args = ["-p", "{prompt}"]
            },
            runtimeId,
            Path.GetTempPath(),
            attemptNumber,
            maxAttempts,
            prompt);
    }

    private static RuntimeAttemptResult CreateUnknownResult()
    {
        return new RuntimeAttemptResult(
            Parsed: UnknownParsed(),
            Artifacts: Array.Empty<ArtifactRef>(),
            Transcript: string.Empty);
    }

    private static StructuredTaskResultParser.ParsedTaskResult UnknownParsed()
    {
        return new StructuredTaskResultParser.ParsedTaskResult(
            HasEnvelope: false,
            EnvelopeTaskId: null,
            Outcome: StructuredTaskResultParser.ParsedTaskOutcome.Unknown,
            Summary: null,
            FailureReason: null,
            NoOp: false,
            Subplan: null,
            ExpectedArtifactTypes: Array.Empty<ArtifactType>());
    }

    private static StructuredTaskResultParser.ParsedTaskResult CompletedParsed(IReadOnlyList<ArtifactType> expectedArtifacts)
    {
        return new StructuredTaskResultParser.ParsedTaskResult(
            HasEnvelope: true,
            EnvelopeTaskId: "task-1",
            Outcome: StructuredTaskResultParser.ParsedTaskOutcome.Completed,
            Summary: "done",
            FailureReason: null,
            NoOp: false,
            Subplan: null,
            ExpectedArtifactTypes: expectedArtifacts);
    }
}
