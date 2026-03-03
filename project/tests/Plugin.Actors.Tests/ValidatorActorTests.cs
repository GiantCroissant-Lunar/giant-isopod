using Akka.Actor;
using Akka.TestKit.Xunit2;
using GiantIsopod.Contracts.Core;
using GiantIsopod.Plugin.Actors;
using GiantIsopod.Plugin.Process;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit;

namespace GiantIsopod.Plugin.Actors.Tests;

public class ValidatorActorTests : TestKit
{
    private readonly IActorRef _artifactRegistryProbe;
    private readonly IActorRef _validator;

    public ValidatorActorTests()
    {
        _artifactRegistryProbe = CreateTestProbe().Ref;

        _validator = Sys.ActorOf(Props.Create(() =>
            new ValidatorActor(
                _artifactRegistryProbe,
                NullLogger<ValidatorActor>.Instance)));
    }

    private static ArtifactRef MakeArtifact(string id, ArtifactType type = ArtifactType.Code, string uri = "/tmp/test.cs") =>
        new(id, type, "text/plain", uri, null,
            new ArtifactProvenance("task-1", "agent-1", DateTimeOffset.UtcNow));

    private static ValidatorSpec ScriptSpec(string name, ArtifactType appliesTo, string command) =>
        new(name, ValidatorKind.Script, appliesTo, command);

    private static ValidatorSpec AgentReviewSpec(
        string name,
        ArtifactType appliesTo,
        string runtimeId,
        string? rubric = null,
        IReadOnlyDictionary<string, string>? config = null) =>
        new(name, ValidatorKind.AgentReview, appliesTo, runtimeId, rubric, config);

    private static string PassingCommand() =>
        OperatingSystem.IsWindows() ? "cmd /c exit 0" : "sh -c true";

    private static string FailingCommand() =>
        OperatingSystem.IsWindows() ? "cmd /c exit 1" : "sh -c false";

    private static RuntimeRegistry ReviewRuntimeRegistry(string runtimeId, string reviewEnvelope, bool usePromptFile = false)
    {
        object runtime = OperatingSystem.IsWindows()
            ? new
            {
                type = "cli",
                id = runtimeId,
                displayName = runtimeId,
                executable = usePromptFile ? "powershell" : "cmd",
                args = usePromptFile
                    ? new[]
                    {
                        "-NoProfile",
                        "-Command",
                        $"Get-Content -Raw '{{prompt_file_path}}' | Out-Null; Write-Output '{reviewEnvelope.Replace("'", "''", StringComparison.Ordinal)}'"
                    }
                    : new[] { "/c", "echo", reviewEnvelope }
            }
            : new
            {
                type = "cli",
                id = runtimeId,
                displayName = runtimeId,
                executable = "sh",
                args = usePromptFile
                    ? new[]
                    {
                        "-c",
                        $"cat \"{{prompt_file_path}}\" >/dev/null && printf '%s\\n' '{reviewEnvelope.Replace("'", "'\\''", StringComparison.Ordinal)}'"
                    }
                    : new[] { "-c", $"printf '%s\\n' '{reviewEnvelope.Replace("'", "'\\''", StringComparison.Ordinal)}'" }
            };

        var json = JsonSerializer.Serialize(new { runtimes = new[] { runtime } });
        return RuntimeRegistry.LoadFromJson(json);
    }

    private static AgentWorldConfig ReviewConfig(string runtimeId, string reviewEnvelope, bool usePromptFile = false)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"validator-review-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        return new AgentWorldConfig
        {
            SkillsBasePath = tempRoot,
            MemoryBasePath = tempRoot,
            AgentDataPath = tempRoot,
            Runtimes = ReviewRuntimeRegistry(runtimeId, reviewEnvelope, usePromptFile),
            DefaultRuntimeId = runtimeId,
            RuntimeWorkingDirectory = tempRoot,
            RuntimeEnvironment = new Dictionary<string, string>()
        };
    }

    private static ArtifactRef MakeTempTextArtifact(string id, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"validator-artifact-{Guid.NewGuid():N}.cs");
        File.WriteAllText(path, content);
        return MakeArtifact(id, uri: path);
    }

    // ── Registration ──

    [Fact]
    public void RegisterValidator_RepliesConfirmation()
    {
        var spec = ScriptSpec("lint", ArtifactType.Code, "echo ok");
        _validator.Tell(new RegisterValidator(spec), TestActor);

        var reply = ExpectMsg<ValidatorRegistered>();
        Assert.Equal("lint", reply.Name);
    }

    // ── No validators ──

    [Fact]
    public void ValidateArtifact_NoValidators_ReturnsEmptyResults()
    {
        var artifact = MakeArtifact("art-1");
        _validator.Tell(new ValidateArtifact("art-1", artifact), TestActor);

        var result = ExpectMsg<ValidationComplete>();
        Assert.Equal("art-1", result.ArtifactId);
        Assert.Empty(result.Results);
    }

    // ── Script validators ──

    [Fact]
    public void ScriptValidator_PassingCommand_ReturnsPass()
    {
        var spec = ScriptSpec("check", ArtifactType.Code, PassingCommand());
        _validator.Tell(new RegisterValidator(spec), TestActor);
        ExpectMsg<ValidatorRegistered>();

        var artifact = MakeArtifact("art-pass");
        _validator.Tell(new ValidateArtifact("art-pass", artifact), TestActor);

        var result = ExpectMsg<ValidationComplete>(TimeSpan.FromSeconds(10));
        Assert.Equal("art-pass", result.ArtifactId);
        Assert.Single(result.Results);
        Assert.True(result.Results[0].Passed);
        Assert.Equal("check", result.Results[0].ValidatorName);
    }

    [Fact]
    public void ScriptValidator_FailingCommand_ReturnsFail()
    {
        var spec = ScriptSpec("fail-check", ArtifactType.Code, FailingCommand());
        _validator.Tell(new RegisterValidator(spec), TestActor);
        ExpectMsg<ValidatorRegistered>();

        var artifact = MakeArtifact("art-fail");
        _validator.Tell(new ValidateArtifact("art-fail", artifact), TestActor);

        var result = ExpectMsg<ValidationComplete>(TimeSpan.FromSeconds(10));
        Assert.Equal("art-fail", result.ArtifactId);
        Assert.Single(result.Results);
        Assert.False(result.Results[0].Passed);
        Assert.Equal("fail-check", result.Results[0].ValidatorName);
    }

    // ── Filtering ──

    [Fact]
    public void ValidateArtifact_FiltersRequiredValidators()
    {
        _validator.Tell(new RegisterValidator(ScriptSpec("v1", ArtifactType.Code, PassingCommand())), TestActor);
        ExpectMsg<ValidatorRegistered>();
        _validator.Tell(new RegisterValidator(ScriptSpec("v2", ArtifactType.Code, PassingCommand())), TestActor);
        ExpectMsg<ValidatorRegistered>();

        var artifact = MakeArtifact("art-filter");
        // Only request v1
        _validator.Tell(new ValidateArtifact("art-filter", artifact,
            RequiredValidators: new[] { "v1" }), TestActor);

        var result = ExpectMsg<ValidationComplete>(TimeSpan.FromSeconds(10));
        Assert.Single(result.Results);
        Assert.Equal("v1", result.Results[0].ValidatorName);
    }

    [Fact]
    public void ValidateArtifact_MissingRequiredValidator_ReturnsFailure()
    {
        _validator.Tell(new RegisterValidator(ScriptSpec("v1", ArtifactType.Code, PassingCommand())), TestActor);
        ExpectMsg<ValidatorRegistered>();

        var artifact = MakeArtifact("art-missing");
        // Request v2 which is not registered
        _validator.Tell(new ValidateArtifact("art-missing", artifact,
            RequiredValidators: new[] { "v2" }), TestActor);

        var result = ExpectMsg<ValidationComplete>(TimeSpan.FromSeconds(10));
        Assert.Single(result.Results);
        Assert.False(result.Results[0].Passed);
        Assert.Equal("v2", result.Results[0].ValidatorName);
    }

    // ── UpdateValidation forwarding ──

    [Fact]
    public void ValidateArtifact_SendsUpdateValidation()
    {
        var registryProbe = CreateTestProbe();
        var validator = Sys.ActorOf(Props.Create(() =>
            new ValidatorActor(registryProbe.Ref, NullLogger<ValidatorActor>.Instance)));

        validator.Tell(new RegisterValidator(ScriptSpec("fwd", ArtifactType.Code, PassingCommand())), TestActor);
        ExpectMsg<ValidatorRegistered>();

        var artifact = MakeArtifact("art-fwd");
        validator.Tell(new ValidateArtifact("art-fwd", artifact), TestActor);

        // Artifact registry should receive UpdateValidation
        var update = registryProbe.ExpectMsg<UpdateValidation>(TimeSpan.FromSeconds(10));
        Assert.Equal("art-fwd", update.ArtifactId);
        Assert.True(update.Result.Passed);

        // Requester also gets ValidationComplete
        ExpectMsg<ValidationComplete>();
    }

    [Fact]
    public void AgentReviewValidator_PassingEnvelope_ReturnsPass()
    {
        const string runtimeId = "review-pass";
        const string artifactId = "art-review-pass";
        var config = ReviewConfig(
            runtimeId,
            "<giant-isopod-review>{\"validator\":\"reviewer\",\"artifact_id\":\"art-review-pass\",\"passed\":true,\"summary\":\"Looks good.\",\"issues\":[]}</giant-isopod-review>");
        var validator = Sys.ActorOf(Props.Create(() =>
            new ValidatorActor(_artifactRegistryProbe, config, NullLogger<ValidatorActor>.Instance)));

        validator.Tell(new RegisterValidator(AgentReviewSpec("reviewer", ArtifactType.Code, runtimeId, "Check correctness.")), TestActor);
        ExpectMsg<ValidatorRegistered>();

        var artifact = MakeTempTextArtifact(artifactId, "public static class Demo { }");
        validator.Tell(new ValidateArtifact(artifactId, artifact), TestActor);

        var result = ExpectMsg<ValidationComplete>(TimeSpan.FromSeconds(10));
        Assert.Single(result.Results);
        Assert.True(result.Results[0].Passed);
        Assert.Equal("reviewer", result.Results[0].ValidatorName);
        Assert.Contains("Looks good.", result.Results[0].Details, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentReviewValidator_FailingEnvelope_ReturnsFail()
    {
        const string runtimeId = "review-fail";
        const string artifactId = "art-review-fail";
        var config = ReviewConfig(
            runtimeId,
            "<giant-isopod-review>{\"validator\":\"reviewer\",\"artifact_id\":\"art-review-fail\",\"passed\":false,\"summary\":\"Found a defect.\",\"issues\":[\"Missing null guard\"]}</giant-isopod-review>");
        var validator = Sys.ActorOf(Props.Create(() =>
            new ValidatorActor(_artifactRegistryProbe, config, NullLogger<ValidatorActor>.Instance)));

        validator.Tell(new RegisterValidator(AgentReviewSpec("reviewer", ArtifactType.Code, runtimeId, "Check correctness.")), TestActor);
        ExpectMsg<ValidatorRegistered>();

        var artifact = MakeTempTextArtifact(artifactId, "public static class Demo { }");
        validator.Tell(new ValidateArtifact(artifactId, artifact), TestActor);

        var result = ExpectMsg<ValidationComplete>(TimeSpan.FromSeconds(10));
        Assert.Single(result.Results);
        Assert.False(result.Results[0].Passed);
        Assert.Equal("reviewer", result.Results[0].ValidatorName);
        Assert.Contains("Found a defect.", result.Results[0].Details, StringComparison.Ordinal);
        Assert.Contains("Missing null guard", result.Results[0].Details, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentReviewValidator_PromptFileRuntime_ReturnsPass()
    {
        const string runtimeId = "review-prompt-file";
        const string artifactId = "art-review-prompt-file";
        var config = ReviewConfig(
            runtimeId,
            "<giant-isopod-review>{\"validator\":\"reviewer\",\"artifact_id\":\"art-review-prompt-file\",\"passed\":true,\"summary\":\"Used prompt file.\",\"issues\":[]}</giant-isopod-review>",
            usePromptFile: true);
        var validator = Sys.ActorOf(Props.Create(() =>
            new ValidatorActor(_artifactRegistryProbe, config, NullLogger<ValidatorActor>.Instance)));

        validator.Tell(new RegisterValidator(AgentReviewSpec("reviewer", ArtifactType.Code, runtimeId, "Check correctness.")), TestActor);
        ExpectMsg<ValidatorRegistered>();

        var artifact = MakeTempTextArtifact(artifactId, "public static class Demo { }");
        validator.Tell(new ValidateArtifact(artifactId, artifact), TestActor);

        var result = ExpectMsg<ValidationComplete>(TimeSpan.FromSeconds(10));
        Assert.Single(result.Results);
        Assert.True(result.Results[0].Passed);
        Assert.Contains("Used prompt file.", result.Results[0].Details, StringComparison.Ordinal);
    }
}

// ── TaskGraph validation integration tests ──

public class TaskGraphValidationTests : TestKit
{
    private readonly Akka.TestKit.TestProbe _dispatchProbe;
    private readonly Akka.TestKit.TestProbe _agentSupervisorProbe;
    private readonly Akka.TestKit.TestProbe _viewportProbe;
    private readonly Akka.TestKit.TestProbe _workspaceProbe;
    private readonly Akka.TestKit.TestProbe _validatorProbe;
    private readonly IActorRef _taskGraph;

    public TaskGraphValidationTests()
    {
        _dispatchProbe = CreateTestProbe();
        _agentSupervisorProbe = CreateTestProbe();
        _viewportProbe = CreateTestProbe();
        _workspaceProbe = CreateTestProbe();
        _validatorProbe = CreateTestProbe();

        _taskGraph = Sys.ActorOf(Props.Create(() =>
            new TaskGraphActor(
                _dispatchProbe.Ref,
                _agentSupervisorProbe.Ref,
                _viewportProbe.Ref,
                _workspaceProbe.Ref,
                _validatorProbe.Ref,
                NullLogger<TaskGraphActor>.Instance)));
    }

    private static HashSet<string> Caps(params string[] caps) => new(caps);

    private static TaskNode Node(string id, string desc = "test task", params string[] caps) =>
        new(id, desc, new HashSet<string>(caps));

    private static ArtifactRef MakeArtifact(string id, ArtifactType type = ArtifactType.Doc) =>
        new(id, type, "text/plain", "/tmp/test", null,
            new ArtifactProvenance("task-1", "agent-1", DateTimeOffset.UtcNow));

    private static ArtifactRef MakeCodeArtifact(string id) =>
        MakeArtifact(id, ArtifactType.Code);

    [Fact]
    public void TaskCompleted_WithArtifacts_SendsValidateToValidator()
    {
        _taskGraph.Tell(new SubmitTaskGraph("g1", new[] { Node("t1") }, Array.Empty<TaskEdge>()), TestActor);
        ExpectMsg<TaskGraphAccepted>();

        // Consume dispatch message (task dispatched)
        _dispatchProbe.ExpectMsg<TaskRequest>();
        _viewportProbe.ExpectMsg<NotifyTaskGraphSubmitted>();
        _viewportProbe.ExpectMsg<NotifyTaskNodeStatusChanged>(); // Dispatched

        var artifact = MakeArtifact("a1");
        _taskGraph.Tell(new TaskCompleted("t1", "agent-1", true, "done", "g1",
            Artifacts: new[] { artifact }));

        // Validator should receive ValidateArtifact
        var validate = _validatorProbe.ExpectMsg<ValidateArtifact>(TimeSpan.FromSeconds(5));
        Assert.Equal("a1", validate.ArtifactId);
        Assert.Equal("t1", validate.TaskId);
    }

    [Fact]
    public void TaskCompleted_WithArtifacts_EmitsValidatorStartRuntimeOutput()
    {
        _taskGraph.Tell(new SubmitTaskGraph("g1b", new[] { Node("t1") }, Array.Empty<TaskEdge>()), TestActor);
        ExpectMsg<TaskGraphAccepted>();

        _dispatchProbe.ExpectMsg<TaskRequest>();
        _viewportProbe.ExpectMsg<NotifyTaskGraphSubmitted>();
        _viewportProbe.ExpectMsg<NotifyTaskNodeStatusChanged>();

        var artifact = MakeArtifact("a1b");
        _taskGraph.Tell(new TaskCompleted("t1", "agent-1", true, "done", "g1b",
            Artifacts: new[] { artifact }));

        _validatorProbe.ExpectMsg<ValidateArtifact>(TimeSpan.FromSeconds(5));
        var validating = _viewportProbe.ExpectMsg<NotifyTaskNodeStatusChanged>(TimeSpan.FromSeconds(5));
        Assert.Equal(TaskNodeStatus.Validating, validating.Status);
        var runtimeOutput = _viewportProbe.ExpectMsg<RuntimeOutput>(TimeSpan.FromSeconds(5));
        Assert.Equal("agent-1", runtimeOutput.AgentId);
        Assert.Contains("[validator]", runtimeOutput.Line, StringComparison.Ordinal);
        Assert.Contains("entered validation", runtimeOutput.Line, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidationComplete_AllPass_CompletesTask()
    {
        Sys.EventStream.Subscribe(TestActor, typeof(TaskGraphCompleted));

        _taskGraph.Tell(new SubmitTaskGraph("g2", new[] { Node("t1") }, Array.Empty<TaskEdge>()), TestActor);
        ExpectMsg<TaskGraphAccepted>();
        _dispatchProbe.ExpectMsg<TaskRequest>();

        var artifact = MakeArtifact("a2");
        _taskGraph.Tell(new TaskCompleted("t1", "agent-1", true, "done", "g2",
            Artifacts: new[] { artifact }));

        _validatorProbe.ExpectMsg<ValidateArtifact>();

        // Reply with passing validation
        _taskGraph.Tell(new ValidationComplete("a2", new[] { new ValidatorResult("lint", true) }));

        var completed = ExpectMsg<TaskGraphCompleted>(TimeSpan.FromSeconds(5));
        Assert.Equal("g2", completed.GraphId);
        Assert.True(completed.Results["t1"]);
    }

    [Fact]
    public void ValidationComplete_AllPass_CodeArtifacts_RequestsMerge()
    {
        _taskGraph.Tell(new SubmitTaskGraph("g3", new[] { Node("t1") }, Array.Empty<TaskEdge>()), TestActor);
        ExpectMsg<TaskGraphAccepted>();
        _dispatchProbe.ExpectMsg<TaskRequest>();

        var artifact = MakeCodeArtifact("a3");
        _taskGraph.Tell(new TaskCompleted("t1", "agent-1", true, "done", "g3",
            Artifacts: new[] { artifact }));

        _validatorProbe.ExpectMsg<ValidateArtifact>();

        // Reply with passing validation
        _taskGraph.Tell(new ValidationComplete("a3", new[] { new ValidatorResult("lint", true) }));

        // Should request merge since it's a code artifact
        var merge = _workspaceProbe.ExpectMsg<RequestMerge>(TimeSpan.FromSeconds(5));
        Assert.Equal("t1", merge.TaskId);
    }

    [Fact]
    public void ValidationComplete_Failure_RedispatchesTask()
    {
        _taskGraph.Tell(new SubmitTaskGraph("g4", new[] { Node("t1") }, Array.Empty<TaskEdge>()), TestActor);
        ExpectMsg<TaskGraphAccepted>();
        _dispatchProbe.ExpectMsg<TaskRequest>();
        _viewportProbe.ExpectMsg<NotifyTaskGraphSubmitted>();
        _viewportProbe.ExpectMsg<NotifyTaskNodeStatusChanged>(); // Dispatched

        var artifact = MakeArtifact("a4");
        _taskGraph.Tell(new TaskCompleted("t1", "agent-1", true, "done", "g4",
            Artifacts: new[] { artifact }));

        _validatorProbe.ExpectMsg<ValidateArtifact>();
        _viewportProbe.ExpectMsg<NotifyTaskNodeStatusChanged>(); // Validating
        _viewportProbe.ExpectMsg<RuntimeOutput>(); // validation started

        // Reply with failed validation
        _taskGraph.Tell(new ValidationComplete("a4", new[] { new ValidatorResult("lint", false, "syntax error") }));

        var runtimeOutput = _viewportProbe.ExpectMsg<RuntimeOutput>(TimeSpan.FromSeconds(5));
        Assert.Equal("agent-1", runtimeOutput.AgentId);
        Assert.Contains("[validator]", runtimeOutput.Line, StringComparison.Ordinal);
        Assert.Contains("syntax error", runtimeOutput.Line, StringComparison.Ordinal);

        // Should re-dispatch with revised description
        var request = _dispatchProbe.ExpectMsg<TaskRequest>(TimeSpan.FromSeconds(5));
        Assert.Equal("t1", request.TaskId);
        Assert.Contains("REVISION", request.Description);
        Assert.Contains("syntax error", request.Description);
    }

    [Fact]
    public void ValidationComplete_MaxAttempts_TaskFails()
    {
        // Use MaxValidationAttempts=2 (default)
        var node = new TaskNode("t1", "test task", new HashSet<string>(), MaxValidationAttempts: 2);
        _taskGraph.Tell(new SubmitTaskGraph("g5", new[] { node }, Array.Empty<TaskEdge>()), TestActor);
        ExpectMsg<TaskGraphAccepted>();
        _dispatchProbe.ExpectMsg<TaskRequest>();

        // First attempt: fails validation
        var artifact1 = MakeArtifact("a5-1");
        _taskGraph.Tell(new TaskCompleted("t1", "agent-1", true, "done", "g5",
            Artifacts: new[] { artifact1 }));
        _validatorProbe.ExpectMsg<ValidateArtifact>();
        _taskGraph.Tell(new ValidationComplete("a5-1", new[] { new ValidatorResult("lint", false, "error") }));

        // Should re-dispatch (attempt 1 < 2)
        _dispatchProbe.ExpectMsg<TaskRequest>();

        // Second attempt: fails validation again
        var artifact2 = MakeArtifact("a5-2");
        _taskGraph.Tell(new TaskCompleted("t1", "agent-1", true, "done", "g5",
            Artifacts: new[] { artifact2 }));
        _validatorProbe.ExpectMsg<ValidateArtifact>();

        Sys.EventStream.Subscribe(TestActor, typeof(TaskGraphCompleted));
        _taskGraph.Tell(new ValidationComplete("a5-2", new[] { new ValidatorResult("lint", false, "error again") }));

        var release = _workspaceProbe.ExpectMsg<ReleaseWorkspace>(TimeSpan.FromSeconds(5));
        Assert.Equal("t1", release.TaskId);

        // Should fail the task (attempt 2 >= 2)
        var completed = ExpectMsg<TaskGraphCompleted>(TimeSpan.FromSeconds(5));
        Assert.Equal("g5", completed.GraphId);
        Assert.False(completed.Results["t1"]);
    }

    [Fact]
    public void TaskCompleted_NoArtifacts_SkipsValidation()
    {
        Sys.EventStream.Subscribe(TestActor, typeof(TaskGraphCompleted));

        _taskGraph.Tell(new SubmitTaskGraph("g6", new[] { Node("t1") }, Array.Empty<TaskEdge>()), TestActor);
        ExpectMsg<TaskGraphAccepted>();
        _dispatchProbe.ExpectMsg<TaskRequest>();

        // Complete without artifacts — should skip validation entirely
        _taskGraph.Tell(new TaskCompleted("t1", "agent-1", true, "done", "g6"));

        var completed = ExpectMsg<TaskGraphCompleted>(TimeSpan.FromSeconds(5));
        Assert.True(completed.Results["t1"]);

        // Validator should NOT have received anything
        _validatorProbe.ExpectNoMsg(TimeSpan.FromMilliseconds(300));
    }
}
