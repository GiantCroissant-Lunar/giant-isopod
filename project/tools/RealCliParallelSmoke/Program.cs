using System.Collections.Concurrent;
using Akka.Actor;
using CliWrap;
using CliWrap.Buffered;
using GiantIsopod.Contracts.Core;
using GiantIsopod.Plugin.Actors;
using GiantIsopod.Plugin.Process;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

var runtimeCsv = args.Length > 0 ? args[0] : "pi,kimi";
var agentsPerRuntime = args.Length > 1 && int.TryParse(args[1], out var parsedAgentsPerRuntime) ? parsedAgentsPerRuntime : 2;
var taskCount = args.Length > 2 && int.TryParse(args[2], out var parsedTaskCount) ? parsedTaskCount : 4;
var timeoutMinutes = args.Length > 3 && int.TryParse(args[3], out var parsedTimeoutMinutes) ? parsedTimeoutMinutes : 12;

var runtimesToUse = runtimeCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();

if (runtimesToUse.Length == 0)
{
    Console.Error.WriteLine("Specify at least one runtime id.");
    return 1;
}

if (taskCount < 2)
{
    Console.Error.WriteLine("Use at least 2 tasks to verify parallel distribution.");
    return 1;
}

var repoRoot = Directory.GetCurrentDirectory();
var runtimesPath = Path.Combine(repoRoot, "project", "hosts", "complete-app", "Data", "Runtimes", "runtimes.json");
if (!File.Exists(runtimesPath))
{
    Console.Error.WriteLine($"Could not find runtimes.json at {runtimesPath}");
    return 1;
}

var tempRoot = Path.Combine(
    Path.GetTempPath(),
    $"giant-isopod-parallel-smoke-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");
var tempRepo = Path.Combine(tempRoot, "repo");
var tempMemory = Path.Combine(tempRoot, "memory");
Directory.CreateDirectory(tempRepo);
Directory.CreateDirectory(tempMemory);

try
{
    await InitializeRepoAsync(tempRepo);

    var runtimes = RuntimeRegistry.LoadFromJson(await File.ReadAllTextAsync(runtimesPath));
    using var loggerFactory = NullLoggerFactory.Instance;

    var config = new AgentWorldConfig
    {
        SkillsBasePath = Path.Combine(repoRoot, "project", "hosts", "complete-app", "Data", "Skills"),
        MemoryBasePath = tempMemory,
        AgentDataPath = Path.Combine(repoRoot, "project", "hosts", "complete-app", "Data", "Agents"),
        Runtimes = runtimes,
        DefaultRuntimeId = runtimesToUse[0],
        RuntimeWorkingDirectory = tempRepo,
        AnchorRepoPath = tempRepo,
        RuntimeEnvironment = BuildRuntimeEnvironment(),
        MemvidExecutable = "memvid",
        MemorySidecarExecutable = "memory-sidecar"
    };

    var bridge = new ParallelSmokeViewportBridge();
    using var world = new AgentWorldSystem(config, loggerFactory);
    world.SetViewportBridge(bridge);

    var profileJson = """
{
  "standard": { "protocol": "AIEOS", "version": "1.2.0" },
  "metadata": { "entity_id": "", "alias": "parallel-smoke" },
  "identity": { "names": { "first": "Parallel", "nickname": "parallel" } },
  "capabilities": {
    "skills": [
      { "name": "code_edit", "description": "Edit source files", "priority": 1 }
    ]
  }
}
""";

    foreach (var runtimeId in runtimesToUse)
    {
        for (var i = 1; i <= agentsPerRuntime; i++)
        {
            var agentId = $"{runtimeId}-parallel-{i}";
            await world.AgentSupervisor.Ask<AgentSpawned>(
                new SpawnAgent(agentId, profileJson, "builder", RuntimeId: runtimeId),
                TimeSpan.FromSeconds(10));
        }
    }

    await Task.Delay(TimeSpan.FromSeconds(3));

    var graphId = $"parallel-{string.Join("-", runtimesToUse)}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
    var nodes = Enumerable.Range(1, taskCount)
        .Select(index => BuildNode(index, runtimesToUse[(index - 1) % runtimesToUse.Length]))
        .ToArray();

    var accepted = await world.TaskGraph.Ask<TaskGraphAccepted>(
        new SubmitTaskGraph(graphId, nodes, Array.Empty<TaskEdge>()),
        TimeSpan.FromSeconds(10));

    Console.WriteLine($"Graph accepted: {accepted.GraphId}");
    var completed = await bridge.WaitForCompletionAsync(graphId, TimeSpan.FromMinutes(timeoutMinutes));

    Console.WriteLine($"Graph completed: {completed.GraphId}");
    foreach (var (taskId, success) in completed.Results.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        Console.WriteLine($"  {taskId}: {(success ? "PASS" : "FAIL")}");

    if (completed.Results.Values.Any(success => !success))
    {
        Console.Error.WriteLine("One or more parallel tasks failed.");
        return 2;
    }

    foreach (var node in nodes)
    {
        var path = Path.Combine(tempRepo, $"ParallelSmoke{node.TaskId}.cs");
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"Expected merged file missing: {path}");
            return 3;
        }
    }

    var assignments = bridge.GetCompletedAssignments(graphId);
    Console.WriteLine("Completed assignments:");
    foreach (var (taskId, agentId) in assignments.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        Console.WriteLine($"  {taskId} -> {agentId}");

    foreach (var node in nodes)
    {
        if (string.IsNullOrWhiteSpace(node.PreferredRuntimeId))
            continue;

        if (!assignments.TryGetValue(node.TaskId, out var assignedAgentId))
        {
            Console.Error.WriteLine($"Missing assignment record for {node.TaskId}.");
            return 5;
        }

        var assignedRuntimeId = GetRuntimePrefix(assignedAgentId);
        if (!string.Equals(assignedRuntimeId, node.PreferredRuntimeId, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                $"Task {node.TaskId} preferred runtime {node.PreferredRuntimeId} but was handled by {assignedRuntimeId}.");
            return 6;
        }
    }

    var uniqueAgents = assignments.Values.Distinct(StringComparer.Ordinal).ToArray();
    var runtimeCounts = uniqueAgents
        .GroupBy(GetRuntimePrefix, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

    Console.WriteLine($"Unique agents used: {uniqueAgents.Length}");
    foreach (var (runtimeId, count) in runtimeCounts.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        Console.WriteLine($"  runtime {runtimeId}: {count} agent(s)");

    var runtimeStarted = bridge.GetRuntimeStarts();
    Console.WriteLine($"Spawned runtime processes observed: {runtimeStarted.Count}");

    if (uniqueAgents.Length < 2)
    {
        Console.Error.WriteLine("Parallel dispatch used fewer than 2 distinct agents.");
        return 4;
    }

    Console.WriteLine("Parallel smoke passed.");
    return 0;
}
finally
{
    TryDeleteDirectory(tempRoot);
}

static TaskNode BuildNode(int index, string preferredRuntimeId)
{
    var taskId = $"task-{index:00}";
    return new TaskNode(
        taskId,
        $"In the current git worktree, create a new file named ParallelSmoke{taskId}.cs containing a public static class ParallelSmoke{taskId.Replace("-", "", StringComparison.Ordinal)} with a public const string Message = \"{taskId}\"; do not modify any other files; then exit.",
        new HashSet<string> { "code_edit" },
        PreferredRuntimeId: preferredRuntimeId);
}

static string GetRuntimePrefix(string agentId)
{
    var marker = "-parallel-";
    var index = agentId.IndexOf(marker, StringComparison.Ordinal);
    return index >= 0 ? agentId[..index] : agentId;
}

static Dictionary<string, string> BuildRuntimeEnvironment()
{
    var env = new Dictionary<string, string>();
    var zaiApiKey = Environment.GetEnvironmentVariable("ZAI_API_KEY");
    if (!string.IsNullOrWhiteSpace(zaiApiKey))
        env["ZAI_API_KEY"] = zaiApiKey;
    return env;
}

static async Task InitializeRepoAsync(string repoPath)
{
    await RunGitAsync(repoPath, "init");
    await RunGitAsync(repoPath, "checkout", "-b", "main");
    await File.WriteAllTextAsync(Path.Combine(repoPath, "README.md"), "# parallel smoke repo\n");
    await RunGitAsync(repoPath, "add", ".");
    await RunGitAsync(repoPath,
        "-c", "user.name=Parallel Smoke",
        "-c", "user.email=parallel-smoke@example.com",
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

sealed class ParallelSmokeViewportBridge : IViewportBridge
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<TaskGraphCompletedEvent>> _graphCompletions = new();
    private readonly ConcurrentDictionary<string, string> _completedAssignments = new();
    private readonly ConcurrentDictionary<string, int> _runtimeStarts = new();
    private readonly ConcurrentDictionary<string, byte> _spawnedAgents = new();

    public Task<TaskGraphCompletedEvent> WaitForCompletionAsync(string graphId, TimeSpan timeout)
    {
        var tcs = _graphCompletions.GetOrAdd(graphId, _ => new TaskCompletionSource<TaskGraphCompletedEvent>(TaskCreationOptions.RunContinuationsAsynchronously));
        return WaitWithTimeoutAsync(tcs.Task, timeout, graphId);
    }

    public IReadOnlyDictionary<string, string> GetCompletedAssignments(string graphId)
    {
        return _completedAssignments
            .Where(kv => kv.Key.StartsWith($"{graphId}:", StringComparison.Ordinal))
            .ToDictionary(
                kv => kv.Key[(graphId.Length + 1)..],
                kv => kv.Value,
                StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, int> GetRuntimeStarts() => _runtimeStarts;

    public void PublishAgentStateChanged(string agentId, AgentActivityState state)
    {
    }

    public void PublishAgentSpawned(string agentId, AgentVisualInfo visualInfo)
    {
        if (!_spawnedAgents.TryAdd(agentId, 0))
            return;

        Console.WriteLine($"[spawned] {agentId}");
    }

    public void PublishAgentDespawned(string agentId)
    {
    }

    public void PublishGenUIRequest(string agentId, string a2uiJson)
    {
    }

    public void PublishRuntimeStarted(string agentId, int processId)
    {
        _runtimeStarts[agentId] = processId;
        Console.WriteLine($"[runtime-started] {agentId}: pid={processId}");
    }

    public void PublishRuntimeExited(string agentId, int exitCode)
    {
    }

    public void PublishRuntimeOutput(string agentId, string line)
    {
        Console.WriteLine($"[runtime] {agentId}: {line}");
    }

    public void PublishTaskGraphSubmitted(string graphId, IReadOnlyList<TaskNode> nodes, IReadOnlyList<TaskEdge> edges)
    {
        Console.WriteLine($"[graph-submitted] {graphId}: nodes={nodes.Count} edges={edges.Count}");
    }

    public void PublishTaskNodeStatusChanged(string graphId, string taskId, TaskNodeStatus status, string? agentId = null)
    {
        Console.WriteLine($"[task-status] {graphId}/{taskId}: {status} agent={agentId ?? "-"}");
        if (status == TaskNodeStatus.Completed && !string.IsNullOrWhiteSpace(agentId))
            _completedAssignments[$"{graphId}:{taskId}"] = agentId;
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
