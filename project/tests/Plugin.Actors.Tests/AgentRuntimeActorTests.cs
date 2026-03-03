using GiantIsopod.Contracts.Core;
using GiantIsopod.Contracts.Protocol.Runtime;
using GiantIsopod.Plugin.Actors;
using Xunit;

namespace GiantIsopod.Plugin.Actors.Tests;

public sealed class AgentRuntimeActorTests
{
    [Fact]
    public void EvaluateFinalResult_AllowsNoOpWhenArtifactsAlreadyExist()
    {
        var execute = new ExecuteTaskPrompt(
            AgentId: "agent-1",
            TaskId: "task-1",
            Prompt: "Do the task.",
            AllowNoOpCompletion: false);
        var attempt = new RuntimeAttemptResult(
            Parsed: CreateParsedResult(
                taskId: "task-1",
                summary: "The task is already satisfied in the task worktree.",
                noOp: true),
            Artifacts:
            [
                CreateArtifact("task-1", "src/Feature.cs")
            ],
            Transcript: "The file already exists and matches the requested state.");

        var result = AgentRuntimeActor.EvaluateFinalResult("agent-1", execute, attempt);

        Assert.Null(result.Failed);
        var completed = Assert.IsType<TaskCompleted>(result.Completed);
        Assert.Equal("task-1", completed.TaskId);
        Assert.Equal("agent-1", completed.AgentId);
        Assert.True(completed.Success);
        Assert.NotNull(completed.Artifacts);
        Assert.Single(completed.Artifacts!);
    }

    [Fact]
    public void EvaluateFinalResult_RejectsNoOpWithoutArtifactsWhenNotAllowed()
    {
        var execute = new ExecuteTaskPrompt(
            AgentId: "agent-1",
            TaskId: "task-2",
            Prompt: "Do the task.",
            AllowNoOpCompletion: false);
        var attempt = new RuntimeAttemptResult(
            Parsed: CreateParsedResult(
                taskId: "task-2",
                summary: "Already satisfied.",
                noOp: true),
            Artifacts: [],
            Transcript: "Nothing to change.");

        var result = AgentRuntimeActor.EvaluateFinalResult("agent-1", execute, attempt);

        Assert.Null(result.Completed);
        var failed = Assert.IsType<TaskFailed>(result.Failed);
        Assert.Contains("does not allow it", failed.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateFinalResult_RejectsExpectedArtifactsWithoutChanges()
    {
        var execute = new ExecuteTaskPrompt(
            AgentId: "agent-1",
            TaskId: "task-3",
            Prompt: "Do the task.",
            AllowNoOpCompletion: false);
        var attempt = new RuntimeAttemptResult(
            Parsed: CreateParsedResult(
                taskId: "task-3",
                summary: "Task completed.",
                expectedArtifactTypes: [ArtifactType.Code]),
            Artifacts: [],
            Transcript: "I said I would create code but did not.");

        var result = AgentRuntimeActor.EvaluateFinalResult("agent-1", execute, attempt);

        Assert.Null(result.Completed);
        var failed = Assert.IsType<TaskFailed>(result.Failed);
        Assert.Contains("expected artifacts", failed.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateFinalResult_AllowsExplicitNoOpWhenTaskPermitsIt()
    {
        var execute = new ExecuteTaskPrompt(
            AgentId: "agent-1",
            TaskId: "task-4",
            Prompt: "Do the task.",
            AllowNoOpCompletion: true);
        var attempt = new RuntimeAttemptResult(
            Parsed: CreateParsedResult(
                taskId: "task-4",
                summary: "Configuration already correct.",
                noOp: true,
                expectedArtifactTypes: [ArtifactType.Config]),
            Artifacts: [],
            Transcript: "No changes needed.");

        var result = AgentRuntimeActor.EvaluateFinalResult("agent-1", execute, attempt);

        Assert.Null(result.Failed);
        var completed = Assert.IsType<TaskCompleted>(result.Completed);
        Assert.Equal("Configuration already correct.", completed.Summary);
    }

    private static StructuredTaskResultParser.ParsedTaskResult CreateParsedResult(
        string taskId,
        string summary,
        bool noOp = false,
        IReadOnlyList<ArtifactType>? expectedArtifactTypes = null)
    {
        return new StructuredTaskResultParser.ParsedTaskResult(
            HasEnvelope: true,
            EnvelopeTaskId: taskId,
            Outcome: StructuredTaskResultParser.ParsedTaskOutcome.Completed,
            Summary: summary,
            FailureReason: null,
            NoOp: noOp,
            Subplan: null,
            ExpectedArtifactTypes: expectedArtifactTypes ?? Array.Empty<ArtifactType>());
    }

    private static ArtifactRef CreateArtifact(string taskId, string relativePath)
    {
        return new ArtifactRef(
            ArtifactId: Guid.NewGuid().ToString("N"),
            Type: ArtifactType.Code,
            Format: "text/plain",
            Uri: $"file:///{relativePath.Replace('\\', '/')}",
            ContentHash: "hash",
            Provenance: new ArtifactProvenance(taskId, "agent-1", DateTimeOffset.UtcNow),
            Metadata: new Dictionary<string, string>
            {
                ["relativePath"] = relativePath
            });
    }
}
