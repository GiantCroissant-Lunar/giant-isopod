using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Akka.Actor;
using GiantIsopod.Contracts.Core;
using GiantIsopod.Contracts.Protocol.AgUi;
using GiantIsopod.Plugin.Actors;
using GiantIsopod.Plugin.Process;
using Microsoft.Extensions.Logging;

var runtimeCsv = args.Length > 0 ? args[0] : "pi";
var timeoutMinutes = args.Length > 1 && int.TryParse(args[1], out var parsedMinutes) ? parsedMinutes : 10;
var taskSpecArg = args.Length > 2 ? args[2] : null;
var taskDescription = args.Length > 2
    ? ResolveTaskDescription(args.Skip(2))
    : """
Add one new unit test method to project/tests/Plugin.Actors.Tests/StructuredTaskResultParserTests.cs.
The test must verify StructuredTaskResultParser.Parse uses the last <giant-isopod-result> envelope when multiple envelopes appear in one transcript.
Edit only that test file. Do not modify production code or any other file.
""";
var runtimesToUse = runtimeCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();
var graphManifest = TryLoadGraphManifest(taskSpecArg);

var timeout = TimeSpan.FromMinutes(timeoutMinutes);
var repoRoot = Directory.GetCurrentDirectory();
var runtimesPath = Path.Combine(repoRoot, "project", "hosts", "complete-app", "Data", "Runtimes", "runtimes.json");
if (!File.Exists(runtimesPath))
{
    Console.Error.WriteLine($"Could not find runtimes.json at {runtimesPath}");
    return 1;
}

var tempMemory = Path.Combine(Path.GetTempPath(), $"giant-isopod-batch-memory-{Guid.NewGuid():N}");
Directory.CreateDirectory(tempMemory);
var ignoredArtifactPaths = BuildIgnoredArtifactPaths(repoRoot, taskSpecArg);

try
{
    var runtimes = RuntimeRegistry.LoadFromJson(await File.ReadAllTextAsync(runtimesPath));
    using var loggerFactory = LoggerFactory.Create(builder =>
    {
        builder
            .SetMinimumLevel(LogLevel.Information)
            .AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
            });
    });

    var config = new AgentWorldConfig
    {
        SkillsBasePath = Path.Combine(repoRoot, "project", "hosts", "complete-app", "Data", "Skills"),
        MemoryBasePath = tempMemory,
        AgentDataPath = Path.Combine(repoRoot, "project", "hosts", "complete-app", "Data", "Agents"),
        Runtimes = runtimes,
        DefaultRuntimeId = runtimesToUse[0],
        RuntimeWorkingDirectory = repoRoot,
        AnchorRepoPath = repoRoot,
        IntegrationBranch = ResolveCurrentBranch(repoRoot),
        RuntimeEnvironment = BuildRuntimeEnvironment(),
        IgnoredArtifactPaths = ignoredArtifactPaths,
        MemvidExecutable = "memvid",
        MemorySidecarExecutable = "memory-sidecar"
    };

    var bridge = new BatchViewportBridge();
    using var world = new AgentWorldSystem(config, loggerFactory);
    world.SetViewportBridge(bridge);

    foreach (var runtimeId in runtimesToUse)
    {
        var agentCount = graphManifest?.AgentsPerRuntime ?? 1;
        for (var i = 1; i <= agentCount; i++)
        {
            var agentId = graphManifest == null || runtimesToUse.Length == 1 && agentCount == 1
                ? $"{runtimeId}-batch"
                : $"{runtimeId}-batch-{i}";
            await world.AgentSupervisor.Ask<AgentSpawned>(
                new SpawnAgent(agentId, BuildAgentProfileJson(runtimeId), "builder", RuntimeId: runtimeId),
                TimeSpan.FromSeconds(10));
        }
    }
    await Task.Delay(TimeSpan.FromSeconds(2));

    TaskNode[] nodes;
    if (graphManifest != null)
    {
        nodes = BuildManifestNodes(graphManifest);
        foreach (var validator in BuildManifestValidators(graphManifest))
        {
            var spec = new ValidatorSpec(
                validator.Name,
                ValidatorKind.AgentReview,
                validator.AppliesTo,
                validator.RuntimeId,
                validator.Rubric,
                new Dictionary<string, string> { ["runtimeId"] = validator.RuntimeId });
            var registered = await world.Validator.Ask<ValidatorRegistered>(
                new RegisterValidator(spec),
                TimeSpan.FromSeconds(10));
            Console.WriteLine($"Validator registered: {registered.Name}");
        }
    }
    else
    {
        nodes =
        [
            new TaskNode(
                "real-task",
                taskDescription,
                new HashSet<string> { "code_edit" },
                RequiredValidators: new[] { "agent-review" })
        ];

        var validatorSpec = new ValidatorSpec(
            Name: "agent-review",
            Kind: ValidatorKind.AgentReview,
            AppliesTo: ArtifactType.Code,
            Command: runtimesToUse[0],
            Rubric: BuildReviewRubric(taskDescription),
            Config: new Dictionary<string, string> { ["runtimeId"] = runtimesToUse[0] });

        var registered = await world.Validator.Ask<ValidatorRegistered>(
            new RegisterValidator(validatorSpec),
            TimeSpan.FromSeconds(10));
        Console.WriteLine($"Validator registered: {registered.Name}");
    }

    var graphId = graphManifest != null
        ? $"{graphManifest.GraphIdPrefix}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}"
        : $"batch-{runtimesToUse[0]}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";

    var edges = graphManifest?.Edges.Select(edge => new TaskEdge(edge.FromTaskId, edge.ToTaskId)).ToArray()
        ?? Array.Empty<TaskEdge>();

    var accepted = await world.TaskGraph.Ask<TaskGraphAccepted>(
        new SubmitTaskGraph(graphId, nodes, edges),
        TimeSpan.FromSeconds(10));

    Console.WriteLine($"Graph accepted: {accepted.GraphId}");
    var completed = await bridge.WaitForCompletionAsync(graphId, timeout);

    Console.WriteLine($"Graph completed: {completed.GraphId}");
    foreach (var (taskId, success) in completed.Results)
        Console.WriteLine($"  {taskId}: {(success ? "PASS" : "FAIL")}");

    if (graphManifest != null)
    {
        PrintBatchSummary(bridge, graphId, nodes);
        var planningSummary = await QueryPlanningSummaryAsync(world, graphManifest, completed);
        PrintPlanningSummary(planningSummary);
        return completed.Results.Values.All(result => result) ? 0 : 4;
    }

    var primaryAgentId = $"{runtimesToUse[0]}-batch";
    var transcript = bridge.GetRuntimeTranscript(primaryAgentId);
    var statusHistory = bridge.GetTaskStatusHistory(graphId, "real-task");

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

    var parsedResult = StructuredTaskResultParser.Parse(transcript, "real-task");
    var memoryQueries = BuildMemoryQueries(taskDescription, parsedResult.Summary);

    var artifacts = await world.Artifacts.Ask<ArtifactListResult>(
        new GetArtifactsByTask("real-task"),
        TimeSpan.FromSeconds(10));
    PrintArtifactSummary(artifacts);

    var knowledgeSummary = await QueryKnowledgeSummaryAsync(world, primaryAgentId, memoryQueries);
    PrintKnowledgeSummary(knowledgeSummary);

    var memorySummary = await WaitForMemorySummaryAsync(world, tempMemory, primaryAgentId, memoryQueries);
    PrintMemorySummary(memorySummary);
    if (memorySummary.FileExists && memorySummary.SearchResult.Hits.Count == 0)
        await PrintMemoryDiagnosticsAsync(config.MemorySidecarExecutable, memorySummary.FilePath, memoryQueries);

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

static string ResolveTaskDescription(IEnumerable<string> args)
{
    var parts = args.ToArray();
    if (parts.Length == 1 && parts[0].StartsWith("@", StringComparison.Ordinal))
    {
        var filePath = parts[0][1..];
        return File.ReadAllText(filePath, System.Text.Encoding.UTF8);
    }

    return string.Join(" ", parts);
}

static string ResolveCurrentBranch(string repoRoot)
{
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }
    };

    process.StartInfo.ArgumentList.Add("rev-parse");
    process.StartInfo.ArgumentList.Add("--abbrev-ref");
    process.StartInfo.ArgumentList.Add("HEAD");
    process.Start();
    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();

    if (process.ExitCode != 0)
        throw new InvalidOperationException($"Failed to resolve current branch: {stderr}");

    var branch = stdout.Trim();
    return string.IsNullOrWhiteSpace(branch) ? "main" : branch;
}

static IReadOnlyCollection<string> BuildIgnoredArtifactPaths(string repoRoot, string? taskSpecArg)
{
    if (string.IsNullOrWhiteSpace(taskSpecArg) || !taskSpecArg.StartsWith("@", StringComparison.Ordinal))
        return Array.Empty<string>();

    var manifestPath = taskSpecArg[1..];
    if (!TryGetRepoRelativePath(repoRoot, manifestPath, out var relativePath))
        return Array.Empty<string>();

    return new[] { relativePath };
}

static bool TryGetRepoRelativePath(string repoRoot, string path, out string relativePath)
{
    relativePath = string.Empty;

    try
    {
        var fullRepoRoot = Path.GetFullPath(repoRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRepoRoot, StringComparison.OrdinalIgnoreCase))
            return false;

        relativePath = Path.GetRelativePath(repoRoot, fullPath).Replace('\\', '/');
        return !string.IsNullOrWhiteSpace(relativePath);
    }
    catch
    {
        return false;
    }
}

static GraphManifest? TryLoadGraphManifest(string? taskSpecArg)
{
    if (string.IsNullOrWhiteSpace(taskSpecArg) || !taskSpecArg.StartsWith("@", StringComparison.Ordinal))
        return null;

    var filePath = taskSpecArg[1..];
    if (!filePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || !File.Exists(filePath))
        return null;

    var json = File.ReadAllText(filePath);
    return JsonSerializer.Deserialize<GraphManifest>(json, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    });
}

static string BuildReviewRubric(string taskDescription)
{
    return
        $"""
Review the produced code artifact for exact task fidelity, correctness, and scope control.
Judge the artifact against the submitted task below, not against any prior batch run or default example.

Submitted task:
{taskDescription}

Pass only if the artifact satisfies that submitted task and stays appropriately scoped.
Fail if the code changes unrelated files, does not implement the submitted task, or claims completion without matching the requested behavior.
""";
}

static string BuildAgentProfileJson(string runtimeId)
{
    var skills = BuildRuntimeSkillEntries(runtimeId);
    var skillsJson = string.Join(
        "," + Environment.NewLine,
        skills.Select(skill => $"      {{ \"name\": \"{skill.Name}\", \"description\": \"{skill.Description}\", \"priority\": {skill.Priority} }}"));

    return $$"""
{
  "standard": { "protocol": "AIEOS", "version": "1.2.0" },
  "metadata": { "entity_id": "", "alias": "batch-runner" },
  "identity": { "names": { "first": "Batch", "nickname": "runner" } },
  "capabilities": {
    "skills": [
{{skillsJson}}
    ]
  }
}
""";
}

static IReadOnlyList<(string Name, string Description, int Priority)> BuildRuntimeSkillEntries(string runtimeId)
{
    var skills = new List<(string Name, string Description, int Priority)>
    {
        ("code_edit", "Edit source files", 1)
    };

    if (string.Equals(runtimeId, "kimi", StringComparison.OrdinalIgnoreCase))
    {
        skills.Add(("implementation", "Implement production code changes", 1));
    }
    else if (string.Equals(runtimeId, "pi", StringComparison.OrdinalIgnoreCase))
    {
        skills.Add(("testing", "Write and adjust tests", 1));
        skills.Add(("review", "Review and verify task output", 2));
    }
    else if (string.Equals(runtimeId, "claude-code", StringComparison.OrdinalIgnoreCase))
    {
        skills.Add(("task_decompose", "Decompose complex tasks into owned path subtasks", 1));
        skills.Add(("review", "Review and verify task output", 2));
    }

    return skills;
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

static void PrintArtifactSummary(ArtifactListResult artifacts)
{
    Console.WriteLine($"Artifacts registered: {artifacts.Artifacts.Count}");
    foreach (var artifact in artifacts.Artifacts)
    {
        var relativePath = artifact.Metadata != null &&
                           artifact.Metadata.TryGetValue("relativePath", out var path)
            ? path
            : artifact.Uri;
        var validators = artifact.Validators is { Count: > 0 }
            ? string.Join(", ", artifact.Validators.Select(v => $"{v.ValidatorName}={(v.Passed ? "PASS" : "FAIL")}"))
            : "none";
        Console.WriteLine($"  artifact {artifact.ArtifactId}: type={artifact.Type} path={relativePath} validators={validators}");
    }
}

static void PrintBatchSummary(BatchViewportBridge bridge, string graphId, IReadOnlyList<TaskNode> nodes)
{
    Console.WriteLine("Completed assignments:");
    foreach (var (taskId, agentId) in bridge.GetCompletedAssignments(graphId).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        Console.WriteLine($"  {taskId} -> {agentId}");

    foreach (var node in nodes.OrderBy(node => node.TaskId, StringComparer.Ordinal))
    {
        var history = bridge.GetTaskStatusHistory(graphId, node.TaskId);
        Console.WriteLine($"  {node.TaskId} preferred={node.PreferredRuntimeId ?? "<none>"} statuses={string.Join(" -> ", history)}");
    }
}

static void PrintPlanningSummary(KnowledgeSummary summary)
{
    Console.WriteLine($"Planning knowledge query: {summary.Query}");
    Console.WriteLine($"Planning knowledge hits: {summary.Result.Entries.Count}");
    foreach (var entry in summary.Result.Entries.Take(3))
        Console.WriteLine($"  planning relevance={entry.Relevance:F3} category={entry.Category} text={entry.Content}");
}

static void PrintKnowledgeSummary(KnowledgeSummary summary)
{
    Console.WriteLine($"Knowledge query: {summary.Query}");
    Console.WriteLine($"Knowledge hits: {summary.Result.Entries.Count}");
    foreach (var entry in summary.Result.Entries.Take(3))
        Console.WriteLine($"  knowledge relevance={entry.Relevance:F3} category={entry.Category} text={entry.Content}");
}

static void PrintMemorySummary(MemorySummary summary)
{
    Console.WriteLine($"Memory file: {(summary.FileExists ? $"present ({summary.FilePath})" : "missing")}");
    Console.WriteLine($"Memory queries: {string.Join(" | ", summary.Queries)}");
    Console.WriteLine($"Memory query: {summary.Query}");
    Console.WriteLine($"Memory hits: {summary.SearchResult.Hits.Count}");
    foreach (var hit in summary.SearchResult.Hits.Take(3))
        Console.WriteLine($"  memory score={hit.Score:F3} title={hit.Title ?? "<none>"} text={hit.Text}");
}

static async Task<MemorySummary> WaitForMemorySummaryAsync(
    AgentWorldSystem world,
    string memoryBasePath,
    string agentId,
    IReadOnlyList<string> queries)
{
    var mv2Path = Path.Combine(memoryBasePath, $"{agentId}.mv2");
    var queryList = queries.Count > 0 ? queries : ["real-task"];
    var lastQuery = queryList[0];
    var lastResult = new MemorySearchResult(agentId, "real-task", Array.Empty<MemoryHit>());
    var fileExists = false;

    for (var attempt = 1; attempt <= 8; attempt++)
    {
        world.MemorySupervisor.Tell(new CommitMemory(agentId), ActorRefs.NoSender);
        await Task.Delay(TimeSpan.FromSeconds(1));
        fileExists = File.Exists(mv2Path);

        foreach (var query in queryList)
        {
            lastQuery = query;
            lastResult = await world.MemorySupervisor.Ask<MemorySearchResult>(
                new SearchMemory(agentId, query, "real-task", TopK: 5),
                TimeSpan.FromSeconds(15));

            if (lastResult.Hits.Count > 0)
                return new MemorySummary(mv2Path, fileExists, queryList, query, lastResult);
        }
    }

    return new MemorySummary(mv2Path, fileExists, queryList, lastQuery, lastResult);
}

static IReadOnlyList<string> BuildMemoryQueries(string taskDescription, string? summary)
{
    var queries = new List<string>();

    AddQuery(summary);
    AddIdentifiers(summary);
    AddQuery(taskDescription);
    AddIdentifiers(taskDescription);
    AddQuery("real-task");

    return queries;

    void AddQuery(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        var trimmed = value.Trim();
        if (!queries.Contains(trimmed, StringComparer.Ordinal))
            queries.Add(trimmed);
    }

    void AddIdentifiers(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        foreach (Match match in Regex.Matches(value, @"\b[A-Za-z_][A-Za-z0-9_]{6,}\b"))
        {
            var token = match.Value;
            if (!queries.Contains(token, StringComparer.Ordinal))
                queries.Add(token);
        }
    }
}

static async Task<KnowledgeSummary> QueryKnowledgeSummaryAsync(
    AgentWorldSystem world,
    string agentId,
    IReadOnlyList<string> queries)
{
    var queryList = queries.Count > 0 ? queries : ["real-task"];
    KnowledgeResult? lastResult = null;
    var lastQuery = queryList[0];

    foreach (var query in queryList)
    {
        lastQuery = query;
        lastResult = await world.KnowledgeSupervisor.Ask<KnowledgeResult>(
            new QueryKnowledge(agentId, query, TopK: 5),
            TimeSpan.FromSeconds(30));

        if (lastResult.Entries.Count > 0)
            return new KnowledgeSummary(query, lastResult);
    }

    return new KnowledgeSummary(lastQuery, lastResult ?? new KnowledgeResult(agentId, Array.Empty<KnowledgeEntry>()));
}

static async Task<KnowledgeSummary> QueryPlanningSummaryAsync(
    AgentWorldSystem world,
    GraphManifest manifest,
    TaskGraphCompletedEvent completed)
{
    var failedTaskIds = completed.Results.Where(kv => !kv.Value).Select(kv => kv.Key).ToList();
    var queries = failedTaskIds.Count > 0
        ? failedTaskIds
        : manifest.Nodes.Select(node => node.TaskId).ToList();

    KnowledgeResult? lastResult = null;
    var lastQuery = queries[0];

    foreach (var query in queries)
    {
        lastQuery = query;
        lastResult = await world.KnowledgeSupervisor.Ask<KnowledgeResult>(
            new QueryKnowledge("task-planner", query, TopK: 5),
            TimeSpan.FromSeconds(30));

        if (lastResult.Entries.Count > 0)
            return new KnowledgeSummary(query, lastResult);
    }

    return new KnowledgeSummary(lastQuery, lastResult ?? new KnowledgeResult("task-planner", Array.Empty<KnowledgeEntry>()));
}

static async Task PrintMemoryDiagnosticsAsync(string sidecarExecutable, string filePath, IReadOnlyList<string> queries)
{
    foreach (var query in queries.Take(3))
    {
        var find = await RunSidecarCommandAsync(sidecarExecutable, ["episodic-search", query, "--file", filePath, "--top-k", "3"]);
        Console.WriteLine($"Memory diagnostic query: {query}");
        Console.WriteLine($"  exit={find.ExitCode}");
        if (!string.IsNullOrWhiteSpace(find.StandardOutput))
            Console.WriteLine($"  stdout={find.StandardOutput.Trim()}");
        if (!string.IsNullOrWhiteSpace(find.StandardError))
            Console.WriteLine($"  stderr={find.StandardError.Trim()}");
    }

    var probePut = await RunSidecarCommandAsync(
        sidecarExecutable,
        ["episodic-put", "diagnostic memory probe", "--file", filePath, "--title", "batch-diagnostic", "--tag", "source=batch-runner"]);
    Console.WriteLine($"Memory diagnostic put exit={probePut.ExitCode}");
    if (!string.IsNullOrWhiteSpace(probePut.StandardOutput))
        Console.WriteLine(probePut.StandardOutput.Trim());
    if (!string.IsNullOrWhiteSpace(probePut.StandardError))
        Console.WriteLine(probePut.StandardError.Trim());

    var probeFind = await RunSidecarCommandAsync(
        sidecarExecutable,
        ["episodic-search", "diagnostic", "--file", filePath, "--top-k", "3"]);
    Console.WriteLine("Memory diagnostic query: diagnostic");
    Console.WriteLine($"  exit={probeFind.ExitCode}");
    if (!string.IsNullOrWhiteSpace(probeFind.StandardOutput))
        Console.WriteLine($"  stdout={probeFind.StandardOutput.Trim()}");
    if (!string.IsNullOrWhiteSpace(probeFind.StandardError))
        Console.WriteLine($"  stderr={probeFind.StandardError.Trim()}");
}

static async Task<CommandOutput> RunSidecarCommandAsync(string executable, IReadOnlyList<string> args)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = CliExecutableResolver.Resolve(
            executable,
            CliExecutableResolver.SidecarRepoLocalCandidates(executable)),
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    foreach (var arg in args)
        startInfo.ArgumentList.Add(arg);

    using var process = new Process { StartInfo = startInfo };
    process.Start();
    var stdoutTask = process.StandardOutput.ReadToEndAsync();
    var stderrTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();

    return new CommandOutput(process.ExitCode, await stdoutTask, await stderrTask);
}

static TaskNode[] BuildManifestNodes(GraphManifest manifest)
{
    return manifest.Nodes.Select(node =>
    {
        ValidateManifestNodeScope(node);
        var requiredValidators = node.RequiredValidators?.Select(name => $"{name}-{node.TaskId}").ToArray();
        return new TaskNode(
            node.TaskId,
            node.Description,
            new HashSet<string>(node.RequiredCapabilities, StringComparer.Ordinal),
            RequiredValidators: requiredValidators,
            MaxValidationAttempts: node.MaxValidationAttempts ?? 2,
            PreferredRuntimeId: node.PreferredRuntimeId,
            PlannerRequiredCapabilities: node.PlannerRequiredCapabilities is { Count: > 0 }
                ? new HashSet<string>(node.PlannerRequiredCapabilities, StringComparer.Ordinal)
                : null,
            PreferredPlannerRuntimeId: node.PreferredPlannerRuntimeId,
            OwnedPaths: node.OwnedPaths,
            ExpectedFiles: node.ExpectedFiles,
            AllowNoOpCompletion: node.AllowNoOpCompletion ?? false);
    }).ToArray();
}

static void ValidateManifestNodeScope(GraphManifestNode node)
{
    if (node.OwnedPaths is not { Count: > 0 })
        throw new InvalidOperationException($"Manifest node '{node.TaskId}' must declare owned_paths.");

    if (node.ExpectedFiles is not { Count: > 0 })
        throw new InvalidOperationException($"Manifest node '{node.TaskId}' must declare expected_files.");
}

static IEnumerable<GraphManifestValidator> BuildManifestValidators(GraphManifest manifest)
{
    var templates = manifest.Validators.ToDictionary(v => v.Name, StringComparer.Ordinal);
    foreach (var node in manifest.Nodes)
    {
        if (node.RequiredValidators is not { Count: > 0 })
            continue;

        foreach (var validatorName in node.RequiredValidators)
        {
            if (!templates.TryGetValue(validatorName, out var template))
                continue;

            yield return template with
            {
                Name = $"{template.Name}-{node.TaskId}",
                Rubric = BuildGraphTaskReviewRubric(
                    template.Rubric,
                    node.TaskId,
                    node.Description,
                    node.OwnedPaths ?? Array.Empty<string>(),
                    node.ExpectedFiles ?? Array.Empty<string>())
            };
        }
    }
}

static string BuildGraphTaskReviewRubric(
    string rubric,
    string taskId,
    string taskDescription,
    IReadOnlyList<string> ownedPaths,
    IReadOnlyList<string> expectedFiles)
{
    return $"""
{rubric}

Validate only task `{taskId}`.
Submitted task:
{taskDescription}

owned_paths:
{string.Join(Environment.NewLine, ownedPaths.Select(path => $"- {path}"))}

expected_files:
{string.Join(Environment.NewLine, expectedFiles.Select(path => $"- {path}"))}

Judge the artifact against this exact submitted task, not against any other batch node or generic feature summary.
""";
}

sealed record MemorySummary(
    string FilePath,
    bool FileExists,
    IReadOnlyList<string> Queries,
    string Query,
    MemorySearchResult SearchResult);

sealed record CommandOutput(int ExitCode, string StandardOutput, string StandardError);
sealed record KnowledgeSummary(string Query, KnowledgeResult Result);
sealed record GraphManifest(
    [property: JsonPropertyName("graph_id_prefix")] string GraphIdPrefix,
    [property: JsonPropertyName("agents_per_runtime")] int AgentsPerRuntime,
    [property: JsonPropertyName("nodes")] IReadOnlyList<GraphManifestNode> Nodes,
    [property: JsonPropertyName("edges")] IReadOnlyList<GraphManifestEdge> Edges,
    [property: JsonPropertyName("validators")] IReadOnlyList<GraphManifestValidator> Validators);

sealed record GraphManifestNode(
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("required_capabilities")] IReadOnlyList<string> RequiredCapabilities,
    [property: JsonPropertyName("preferred_runtime_id")] string? PreferredRuntimeId,
    [property: JsonPropertyName("planner_required_capabilities")] IReadOnlyList<string>? PlannerRequiredCapabilities,
    [property: JsonPropertyName("preferred_planner_runtime_id")] string? PreferredPlannerRuntimeId,
    [property: JsonPropertyName("required_validators")] IReadOnlyList<string>? RequiredValidators,
    [property: JsonPropertyName("max_validation_attempts")] int? MaxValidationAttempts,
    [property: JsonPropertyName("owned_paths")] IReadOnlyList<string>? OwnedPaths,
    [property: JsonPropertyName("expected_files")] IReadOnlyList<string>? ExpectedFiles,
    [property: JsonPropertyName("allow_no_op_completion")] bool? AllowNoOpCompletion);

sealed record GraphManifestEdge(
    [property: JsonPropertyName("from_task_id")] string FromTaskId,
    [property: JsonPropertyName("to_task_id")] string ToTaskId);

sealed record GraphManifestValidator(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("runtime_id")] string RuntimeId,
    [property: JsonPropertyName("rubric")] string Rubric,
    [property: JsonPropertyName("applies_to")] ArtifactType AppliesTo);

sealed class BatchViewportBridge : IViewportBridge
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<TaskGraphCompletedEvent>> _graphCompletions = new();
    private readonly ConcurrentDictionary<string, TaskNodeStatus> _taskStatuses = new();
    private readonly ConcurrentDictionary<string, List<TaskNodeStatus>> _taskStatusHistory = new();
    private readonly ConcurrentDictionary<string, List<string>> _runtimeOutput = new();
    private readonly ConcurrentDictionary<string, byte> _spawnedAgents = new();
    private readonly ConcurrentDictionary<string, string> _completedAssignments = new();

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

    public IReadOnlyDictionary<string, string> GetCompletedAssignments(string graphId)
    {
        return _completedAssignments
            .Where(kv => kv.Key.StartsWith($"{graphId}:", StringComparison.Ordinal))
            .ToDictionary(
                kv => kv.Key[(graphId.Length + 1)..],
                kv => kv.Value,
                StringComparer.Ordinal);
    }

    public void PublishAgentStateChanged(string agentId, AgentActivityState state)
    {
        Console.WriteLine($"[state] {agentId}: {state}");
    }

    public void PublishAgentSpawned(string agentId, AgentVisualInfo visualInfo)
    {
        if (!_spawnedAgents.TryAdd(agentId, 0))
            return;

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

    public void PublishAgUiEvent(string agentId, object agUiEvent)
    {
        Console.WriteLine($"[ag-ui] {agentId}: {FormatAgUiEvent(agUiEvent)}");
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
        if (status == TaskNodeStatus.Completed && !string.IsNullOrWhiteSpace(agentId))
            _completedAssignments[key] = agentId;
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

    private static string FormatAgUiEvent(object agUiEvent)
    {
        return agUiEvent switch
        {
            RunStartedEvent started => $"RunStarted run={started.RunId}",
            RunFinishedEvent finished => $"RunFinished run={finished.RunId}",
            RunErrorEvent error => $"RunError run={error.RunId} message={error.Message}",
            StepStartedEvent started => $"StepStarted run={started.RunId} step={started.StepName}",
            StepFinishedEvent finished => $"StepFinished run={finished.RunId} step={finished.StepName}",
            CustomEvent custom => $"Custom {custom.Name} {FormatCustomData(custom.Data)}",
            TextMessageStartEvent start => $"TextMessageStart message={start.MessageId}",
            TextMessageContentEvent content => $"TextMessageContent delta={content.Delta}",
            TextMessageEndEvent end => $"TextMessageEnd message={end.MessageId}",
            ToolCallStartEvent tool => $"ToolCallStart tool={tool.ToolName}",
            ToolCallEndEvent toolEnd => $"ToolCallEnd toolCall={toolEnd.ToolCallId}",
            _ => agUiEvent.GetType().Name
        };
    }

    private static string FormatCustomData(IReadOnlyDictionary<string, object?>? data)
    {
        if (data is null || data.Count == 0)
            return string.Empty;

        return string.Join(
            " ",
            data.Select(kv => $"{kv.Key}={kv.Value}"));
    }
}

sealed record TaskGraphCompletedEvent(string GraphId, IReadOnlyDictionary<string, bool> Results);
