using System.Collections.Concurrent;
using Akka.Actor;
using CliWrap;
using CliWrap.Buffered;
using GiantIsopod.Contracts.Core;
using GiantIsopod.Plugin.Actors;
using GiantIsopod.Plugin.Process;
using Microsoft.Extensions.Logging.Abstractions;

var runtimeId = args.Length > 0 ? args[0] : "codex";
var timeoutMinutes = args.Length > 1 && int.TryParse(args[1], out var parsedMinutes) ? parsedMinutes : 6;
var scenario = args.Length > 2 ? args[2] : "basic";
var timeout = TimeSpan.FromMinutes(timeoutMinutes);

var repoRoot = Directory.GetCurrentDirectory();
var runtimesPath = Path.Combine(repoRoot, "project", "hosts", "complete-app", "Data", "Runtimes", "runtimes.json");
if (!File.Exists(runtimesPath))
{
    Console.Error.WriteLine($"Could not find runtimes.json at {runtimesPath}");
    return 1;
}

var tempRoot = Path.Combine(
    Path.GetTempPath(),
    $"giant-isopod-smoke-{runtimeId}-{scenario}-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");
var tempRepo = Path.Combine(tempRoot, "repo");
var tempMemory = Path.Combine(tempRoot, "memory");
Directory.CreateDirectory(tempRepo);
Directory.CreateDirectory(tempMemory);

Console.WriteLine($"Smoke root: {tempRoot}");
Console.WriteLine($"Runtime: {runtimeId}");
Console.WriteLine($"Scenario: {scenario}");

AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    try
    {
        if (Directory.Exists(tempRoot))
            Directory.Delete(tempRoot, recursive: true);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to delete smoke temp directory '{tempRoot}': {ex}");
    }
};

await InitializeRepoAsync(tempRepo);

var runtimes = RuntimeRegistry.LoadFromJson(await File.ReadAllTextAsync(runtimesPath));
var runtimeEnv = BuildRuntimeEnvironment();

var config = new AgentWorldConfig
{
    SkillsBasePath = Path.Combine(repoRoot, "project", "hosts", "complete-app", "Data", "Skills"),
    MemoryBasePath = tempMemory,
    AgentDataPath = Path.Combine(repoRoot, "project", "hosts", "complete-app", "Data", "Agents"),
    Runtimes = runtimes,
    DefaultRuntimeId = runtimeId,
    RuntimeWorkingDirectory = tempRepo,
    AnchorRepoPath = tempRepo,
    RuntimeEnvironment = runtimeEnv,
    MemvidExecutable = "memvid",
    MemorySidecarExecutable = "memory-sidecar"
};

var bridge = new SmokeViewportBridge();
using var world = new AgentWorldSystem(config, NullLoggerFactory.Instance);
world.SetViewportBridge(bridge);

var agentId = $"{runtimeId}-smoke";
var marker = $"smoke-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
var profileJson = """
{
  "standard": { "protocol": "AIEOS", "version": "1.2.0" },
  "metadata": { "entity_id": "", "alias": "smoke" },
  "identity": { "names": { "first": "Smoke", "nickname": "smoke" } },
  "capabilities": {
    "skills": [
      { "name": "code_edit", "description": "Edit source files", "priority": 1 }
    ]
  }
}
""";

world.AgentSupervisor.Tell(new SpawnAgent(agentId, profileJson, "builder", RuntimeId: runtimeId), ActorRefs.NoSender);
await Task.Delay(TimeSpan.FromSeconds(2));

var graphId = $"smoke-{runtimeId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
var node = BuildScenarioNode(scenario, marker);
await RegisterScenarioValidatorAsync(world, scenario, runtimeId, marker);

var accepted = await world.TaskGraph.Ask<TaskGraphAccepted>(
    new SubmitTaskGraph(
        graphId,
        new[] { node },
        Array.Empty<TaskEdge>()),
    TimeSpan.FromSeconds(10));

Console.WriteLine($"Graph accepted: {accepted.GraphId}");

var completed = await bridge.WaitForCompletionAsync(graphId, timeout);
var smokeFile = Path.Combine(tempRepo, "SmokeRuntimeE2E.cs");
var runtimeTranscript = bridge.GetRuntimeTranscript(agentId);
var statusHistory = bridge.GetTaskStatusHistory(graphId, "smoke-edit");

Console.WriteLine($"Graph completed: {completed.GraphId}");
foreach (var (taskId, success) in completed.Results)
    Console.WriteLine($"  {taskId}: {(success ? "PASS" : "FAIL")}");

if (!completed.Results.TryGetValue("smoke-edit", out var taskPassed))
{
    Console.Error.WriteLine("Task graph did not report a result for smoke-edit.");
    return 2;
}

switch (scenario.ToLowerInvariant())
{
    case "basic":
        if (!taskPassed)
        {
            Console.Error.WriteLine("Task graph reported failure.");
            return 2;
        }

        if (!File.Exists(smokeFile))
        {
            Console.Error.WriteLine($"Expected merged file was not found: {smokeFile}");
            return 3;
        }

        var content = await File.ReadAllTextAsync(smokeFile);
        if (!content.Contains(marker, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Merged file did not contain the expected marker.");
            return 4;
        }

        break;

    case "review-pass":
        if (!taskPassed)
        {
            Console.Error.WriteLine("Review-pass scenario unexpectedly failed.");
            return 10;
        }

        if (!File.Exists(smokeFile))
        {
            Console.Error.WriteLine($"Expected merged file was not found: {smokeFile}");
            return 11;
        }

        var reviewPassContent = await File.ReadAllTextAsync(smokeFile);
        if (!reviewPassContent.Contains(marker, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Merged file did not contain the expected marker after review pass.");
            return 12;
        }

        if (!statusHistory.Contains(TaskNodeStatus.Validating))
        {
            Console.Error.WriteLine("Review-pass scenario never entered Validating state.");
            return 13;
        }

        break;

    case "review-fail":
        if (taskPassed)
        {
            Console.Error.WriteLine("Review-fail scenario unexpectedly passed.");
            return 20;
        }

        if (File.Exists(smokeFile))
        {
            Console.Error.WriteLine("Review-fail scenario should not have merged SmokeRuntimeE2E.cs into main.");
            return 21;
        }

        var validatingCount = statusHistory.Count(status => status == TaskNodeStatus.Validating);
        var dispatchedCount = statusHistory.Count(status => status == TaskNodeStatus.Dispatched);
        if (validatingCount < 2 || dispatchedCount < 2)
        {
            Console.Error.WriteLine("Review-fail scenario did not exercise the expected revision loop.");
            return 22;
        }

        break;

    default:
        Console.Error.WriteLine($"Unknown scenario '{scenario}'.");
        return 30;
}

if (!runtimeTranscript.Contains("<giant-isopod-result>", StringComparison.Ordinal))
{
    Console.Error.WriteLine("Runtime output did not contain the structured result envelope.");
    return 5;
}

var log = await RunGitAsync(tempRepo, "log", "--format=%s", "-n", "1");
Console.WriteLine($"Merge head commit: {log.Trim()}");
Console.WriteLine($"Status history: {string.Join(" -> ", statusHistory)}");
Console.WriteLine("Structured envelope detected.");
Console.WriteLine("Smoke test passed.");
return 0;

static Dictionary<string, string> BuildRuntimeEnvironment()
{
    var env = new Dictionary<string, string>();

    var zaiApiKey = Environment.GetEnvironmentVariable("ZAI_API_KEY");
    if (!string.IsNullOrWhiteSpace(zaiApiKey))
        env["ZAI_API_KEY"] = zaiApiKey;

    return env;
}

static TaskNode BuildScenarioNode(string scenario, string marker)
{
    var normalized = scenario.ToLowerInvariant();
    return normalized switch
    {
        "basic" => new TaskNode(
            "smoke-edit",
            $"In the current git worktree, create a new file named SmokeRuntimeE2E.cs containing a public static class SmokeRuntimeE2E with a public const string Message = \"{marker}\"; do not modify any other files; then exit.",
            new HashSet<string> { "code_edit" }),
        "review-pass" => new TaskNode(
            "smoke-edit",
            $"In the current git worktree, create a new file named SmokeRuntimeE2E.cs containing a public static class SmokeRuntimeE2E with a public const string Message = \"{marker}\"; do not modify any other files; then exit.",
            new HashSet<string> { "code_edit" },
            RequiredValidators: new[] { "agent-review" }),
        "review-fail" => new TaskNode(
            "smoke-edit",
            $"In the current git worktree, create a new file named SmokeRuntimeE2E.cs containing a public static class SmokeRuntimeE2E with a public const string Message = \"{marker}\"; do not modify any other files; then exit.",
            new HashSet<string> { "code_edit" },
            RequiredValidators: new[] { "agent-review" },
            MaxValidationAttempts: 2),
        _ => throw new InvalidOperationException($"Unknown scenario '{scenario}'.")
    };
}

static async Task RegisterScenarioValidatorAsync(AgentWorldSystem world, string scenario, string runtimeId, string marker)
{
    var normalized = scenario.ToLowerInvariant();
    if (normalized is not ("review-pass" or "review-fail"))
        return;

    var rubric = normalized == "review-pass"
        ? $"""
Review the artifact for this task.
Pass only if the artifact is a single C# file named SmokeRuntimeE2E.cs containing a public static class SmokeRuntimeE2E and an exact public const string Message = "{marker}".
Fail if the file name, class name, marker value, or scope is wrong, or if unrelated files were modified.
"""
        : """
Always fail this review for the smoke test.
Set passed=false.
Use summary "Forced review failure for smoke test."
Include the actionable issue "Smoke forced review failure".
""";

    var spec = new ValidatorSpec(
        Name: "agent-review",
        Kind: ValidatorKind.AgentReview,
        AppliesTo: ArtifactType.Code,
        Command: runtimeId,
        Rubric: rubric,
        Config: new Dictionary<string, string> { ["runtimeId"] = runtimeId });

    var registered = await world.Validator.Ask<ValidatorRegistered>(
        new RegisterValidator(spec),
        TimeSpan.FromSeconds(10));

    Console.WriteLine($"Validator registered: {registered.Name}");
}

static async Task InitializeRepoAsync(string repoPath)
{
    await RunGitAsync(repoPath, "init");
    await RunGitAsync(repoPath, "checkout", "-b", "main");
    await File.WriteAllTextAsync(Path.Combine(repoPath, "README.md"), "# smoke repo\n");
    await RunGitAsync(repoPath, "add", ".");
    await RunGitAsync(repoPath,
        "-c", "user.name=Smoke Runner",
        "-c", "user.email=smoke@example.com",
        "commit", "-m", "initial commit");
}

static async Task<string> RunGitAsync(string workDir, params string[] args)
{
    var result = await Cli.Wrap("git")
        .WithArguments(args)
        .WithWorkingDirectory(workDir)
        .WithValidation(CommandResultValidation.None)
        .ExecuteBufferedAsync();

    if (result.ExitCode != 0)
        throw new InvalidOperationException($"git {string.Join(" ", args)} failed: {result.StandardError}");

    return result.StandardOutput;
}

sealed class SmokeViewportBridge : IViewportBridge
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<TaskGraphCompletedEvent>> _graphCompletions = new();
    private readonly ConcurrentDictionary<string, TaskNodeStatus> _taskStatuses = new();
    private readonly ConcurrentDictionary<string, List<TaskNodeStatus>> _taskStatusHistory = new();
    private readonly ConcurrentDictionary<string, List<string>> _runtimeOutput = new();

    public Task<TaskGraphCompletedEvent> WaitForCompletionAsync(string graphId, TimeSpan timeout)
    {
        var tcs = _graphCompletions.GetOrAdd(graphId, _ => new TaskCompletionSource<TaskGraphCompletedEvent>(TaskCreationOptions.RunContinuationsAsynchronously));
        return WaitWithTimeoutAsync(tcs.Task, timeout, graphId);
    }

    public string GetRuntimeTranscript(string agentId)
    {
        return !_runtimeOutput.TryGetValue(agentId, out var lines)
            ? string.Empty
            : string.Join(Environment.NewLine, lines);
    }

    public IReadOnlyList<TaskNodeStatus> GetTaskStatusHistory(string graphId, string taskId)
    {
        var key = $"{graphId}:{taskId}";
        if (!_taskStatusHistory.TryGetValue(key, out var history))
            return Array.Empty<TaskNodeStatus>();

        lock (history)
        {
            return history.ToArray();
        }
    }

    public void PublishAgentStateChanged(string agentId, AgentActivityState state)
    {
        Console.WriteLine($"[state] {agentId}: {state}");
    }

    public void PublishAgentSpawned(string agentId, AgentVisualInfo visualInfo)
    {
        Console.WriteLine($"[spawned] {agentId}");
    }

    public void PublishAgentDespawned(string agentId)
    {
        Console.WriteLine($"[despawned] {agentId}");
    }

    public void PublishGenUIRequest(string agentId, string a2uiJson)
    {
    }

    public void PublishRuntimeStarted(string agentId, int processId)
    {
        Console.WriteLine($"[runtime-started] {agentId}: pid={processId}");
    }

    public void PublishRuntimeExited(string agentId, int exitCode)
    {
        Console.WriteLine($"[runtime-exited] {agentId}: exit={exitCode}");
    }

    public void PublishRuntimeOutput(string agentId, string line)
    {
        var lines = _runtimeOutput.GetOrAdd(agentId, _ => new List<string>());
        lock (lines)
        {
            lines.Add(line);
        }
        Console.WriteLine($"[runtime] {agentId}: {line}");
    }

    public void PublishTaskGraphSubmitted(string graphId, IReadOnlyList<TaskNode> nodes, IReadOnlyList<TaskEdge> edges)
    {
        Console.WriteLine($"[graph-submitted] {graphId}: nodes={nodes.Count} edges={edges.Count}");
    }

    public void PublishTaskNodeStatusChanged(string graphId, string taskId, TaskNodeStatus status, string? agentId = null)
    {
        var key = $"{graphId}:{taskId}";
        var history = _taskStatusHistory.GetOrAdd(key, _ => new List<TaskNodeStatus>());
        lock (history)
        {
            history.Add(status);
        }

        if (_taskStatuses.TryGetValue(key, out var previous) && previous == status)
            return;

        _taskStatuses[key] = status;
        Console.WriteLine($"[task-status] {graphId}/{taskId}: {status} agent={agentId ?? "-"}");
    }

    public void PublishTaskGraphCompleted(string graphId, IReadOnlyDictionary<string, bool> results)
    {
        _graphCompletions.GetOrAdd(graphId, _ => new TaskCompletionSource<TaskGraphCompletedEvent>(TaskCreationOptions.RunContinuationsAsynchronously))
            .TrySetResult(new TaskGraphCompletedEvent(graphId, results));
    }

    private static async Task<TaskGraphCompletedEvent> WaitWithTimeoutAsync(
        Task<TaskGraphCompletedEvent> completionTask,
        TimeSpan timeout,
        string graphId)
    {
        using var cts = new CancellationTokenSource(timeout);
        var delayTask = Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
        var finished = await Task.WhenAny(completionTask, delayTask);
        if (finished != completionTask)
            throw new TimeoutException($"Timed out waiting for graph {graphId} after {timeout}.");

        cts.Cancel();
        return await completionTask;
    }
}

sealed record TaskGraphCompletedEvent(string GraphId, IReadOnlyDictionary<string, bool> Results);
