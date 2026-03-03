using System.Collections.Concurrent;
using Akka.Actor;
using GiantIsopod.Contracts.Core;
using GiantIsopod.Plugin.Actors;
using GiantIsopod.Plugin.Process;
using Microsoft.Extensions.Logging.Abstractions;

var runtimeId = args.Length > 0 ? args[0] : "pi";
var timeoutMinutes = args.Length > 1 && int.TryParse(args[1], out var parsedMinutes) ? parsedMinutes : 10;
var taskDescription = args.Length > 2
    ? string.Join(" ", args.Skip(2))
    : """
Add one new unit test method to project/tests/Plugin.Actors.Tests/StructuredTaskResultParserTests.cs.
The test must verify StructuredTaskResultParser.Parse uses the last <giant-isopod-result> envelope when multiple envelopes appear in one transcript.
Edit only that test file. Do not modify production code or any other file.
""";

var timeout = TimeSpan.FromMinutes(timeoutMinutes);
var repoRoot = Directory.GetCurrentDirectory();
var runtimesPath = Path.Combine(repoRoot, "project", "hosts", "complete-app", "Data", "Runtimes", "runtimes.json");
if (!File.Exists(runtimesPath))
{
    Console.Error.WriteLine($"Could not find runtimes.json at {runtimesPath}");
    return 1;
}

var tempMemory = Path.Combine(Path.GetTempPath(), $"giant-isopod-dogfood-memory-{Guid.NewGuid():N}");
Directory.CreateDirectory(tempMemory);

try
{
    var runtimes = RuntimeRegistry.LoadFromJson(await File.ReadAllTextAsync(runtimesPath));
    var config = new AgentWorldConfig
    {
        SkillsBasePath = Path.Combine(repoRoot, "project", "hosts", "complete-app", "Data", "Skills"),
        MemoryBasePath = tempMemory,
        AgentDataPath = Path.Combine(repoRoot, "project", "hosts", "complete-app", "Data", "Agents"),
        Runtimes = runtimes,
        DefaultRuntimeId = runtimeId,
        RuntimeWorkingDirectory = repoRoot,
        AnchorRepoPath = repoRoot,
        RuntimeEnvironment = BuildRuntimeEnvironment(),
        MemvidExecutable = "memvid",
        MemorySidecarExecutable = "memory-sidecar"
    };

    var bridge = new DogfoodViewportBridge();
    using var world = new AgentWorldSystem(config, NullLoggerFactory.Instance);
    world.SetViewportBridge(bridge);

    var agentId = $"{runtimeId}-dogfood";
    var profileJson = """
{
  "standard": { "protocol": "AIEOS", "version": "1.2.0" },
  "metadata": { "entity_id": "", "alias": "dogfood" },
  "identity": { "names": { "first": "Dogfood", "nickname": "dogfood" } },
  "capabilities": {
    "skills": [
      { "name": "code_edit", "description": "Edit source files", "priority": 1 }
    ]
  }
}
""";

    world.AgentSupervisor.Tell(new SpawnAgent(agentId, profileJson, "builder", RuntimeId: runtimeId), ActorRefs.NoSender);
    await Task.Delay(TimeSpan.FromSeconds(2));

    var validatorSpec = new ValidatorSpec(
        Name: "agent-review",
        Kind: ValidatorKind.AgentReview,
        AppliesTo: ArtifactType.Code,
        Command: "pi",
        Rubric: """
Review the produced code artifact for task fidelity, correctness, and scope control.
Pass only if the change is limited to the requested test file and clearly verifies the intended parser behavior.
Fail if unrelated files were modified, the assertion target is wrong, or the test does not actually prove the last envelope wins.
""",
        Config: new Dictionary<string, string> { ["runtimeId"] = "pi" });

    var registered = await world.Validator.Ask<ValidatorRegistered>(
        new RegisterValidator(validatorSpec),
        TimeSpan.FromSeconds(10));
    Console.WriteLine($"Validator registered: {registered.Name}");

    var graphId = $"dogfood-{runtimeId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
    var node = new TaskNode(
        "real-task",
        taskDescription,
        new HashSet<string> { "code_edit" },
        RequiredValidators: new[] { "agent-review" });

    var accepted = await world.TaskGraph.Ask<TaskGraphAccepted>(
        new SubmitTaskGraph(graphId, new[] { node }, Array.Empty<TaskEdge>()),
        TimeSpan.FromSeconds(10));

    Console.WriteLine($"Graph accepted: {accepted.GraphId}");
    var completed = await bridge.WaitForCompletionAsync(graphId, timeout);
    var transcript = bridge.GetRuntimeTranscript(agentId);
    var statusHistory = bridge.GetTaskStatusHistory(graphId, "real-task");

    Console.WriteLine($"Graph completed: {completed.GraphId}");
    foreach (var (taskId, success) in completed.Results)
        Console.WriteLine($"  {taskId}: {(success ? "PASS" : "FAIL")}");

    if (!completed.Results.TryGetValue("real-task", out var taskPassed))
    {
        Console.Error.WriteLine("Task graph did not report a result for real-task.");
        return 2;
    }

    if (!transcript.Contains("<giant-isopod-result>", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("Runtime output did not contain the structured result envelope.");
        return 3;
    }

    Console.WriteLine($"Status history: {string.Join(" -> ", statusHistory)}");
    return taskPassed ? 0 : 4;
}
finally
{
    TryDeleteDirectory(tempMemory);
}

static Dictionary<string, string> BuildRuntimeEnvironment()
{
    var env = new Dictionary<string, string>();
    var zaiApiKey = Environment.GetEnvironmentVariable("ZAI_API_KEY");
    if (!string.IsNullOrWhiteSpace(zaiApiKey))
        env["ZAI_API_KEY"] = zaiApiKey;
    return env;
}

static void TryDeleteDirectory(string path)
{
    if (!Directory.Exists(path))
        return;

    try
    {
        Directory.Delete(path, recursive: true);
    }
    catch
    {
    }
}

sealed class DogfoodViewportBridge : IViewportBridge
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
