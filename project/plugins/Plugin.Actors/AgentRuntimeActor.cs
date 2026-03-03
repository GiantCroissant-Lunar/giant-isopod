using Akka.Actor;
using GiantIsopod.Contracts.Core;
using GiantIsopod.Plugin.Process;
using System.Text;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// /user/agents/{name}/rpc — executes task prompts against the configured runtime.
/// Each task execution creates a fresh runtime bound to the task's workspace and
/// streams output as RuntimeEvent messages to the parent AgentActor.
/// </summary>
public sealed class AgentRuntimeActor : UntypedActor
{
    private const int MaxExecutionAttempts = 3;
    private readonly string _agentId;
    private readonly string? _runtimeId;
    private readonly ModelSpec? _model;
    private readonly AgentWorldConfig _config;
    private IAgentRuntime? _runtime;
    private CancellationTokenSource? _lifetimeCts;
    private CancellationTokenSource? _executionCts;
    private bool _runtimeStarted;

    private readonly Dictionary<string, TokenBudgetState> _tokenBudgets = new();
    private string? _activeTaskId;

    public AgentRuntimeActor(string agentId, AgentWorldConfig config, string? runtimeId = null, ModelSpec? model = null)
    {
        _agentId = agentId;
        _config = config;
        _runtimeId = runtimeId;
        _model = model;
    }

    protected override void OnReceive(object message)
    {
        switch (message)
        {
            case StartRuntime:
                StartRuntimeHost();
                break;

            case ExecuteTaskPrompt execute:
                StartTaskExecution(execute);
                break;

            case SendPrompt prompt when !string.IsNullOrWhiteSpace(prompt.Message):
                StartAdhocExecution(prompt.Message);
                break;

            case RuntimeEvent evt:
                TrackTokenUsage(evt.RawJson);
                Context.Parent.Tell(evt);
                break;

            case SetTokenBudget budget:
                _activeTaskId = budget.TaskId;
                _tokenBudgets[budget.TaskId] = new TokenBudgetState(budget.MaxTokens);
                break;
        }
    }

    private void TrackTokenUsage(string output)
    {
        if (_activeTaskId is null || !_tokenBudgets.TryGetValue(_activeTaskId, out var budgetState))
            return;

        budgetState.CumulativeChars += output.Length;
        var estimatedTokens = (int)(budgetState.CumulativeChars / 4);

        if (estimatedTokens > budgetState.MaxTokens * 1.2)
        {
            Context.ActorSelection("../tasks")
                .Tell(new TokenBudgetExceeded(_activeTaskId, estimatedTokens, budgetState.MaxTokens));
            _executionCts?.Cancel();
        }
        else if (!budgetState.Warned && estimatedTokens > budgetState.MaxTokens)
        {
            budgetState.Warned = true;
            Context.ActorSelection("../tasks")
                .Tell(new TokenBudgetExceeded(_activeTaskId, estimatedTokens, budgetState.MaxTokens));
        }
    }

    private void StartRuntimeHost()
    {
        if (_runtimeStarted)
            return;

        _runtimeStarted = true;
        _lifetimeCts = new CancellationTokenSource();
        Context.Parent.Tell(new RuntimeStarted(_agentId, Environment.ProcessId));
    }

    private void StartTaskExecution(ExecuteTaskPrompt execute)
    {
        if (_executionCts is not null)
        {
            Context.Parent.Tell(new TaskFailed(execute.TaskId, "Runtime is already executing another task", GraphId: execute.GraphId));
            return;
        }

        _executionCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts?.Token ?? CancellationToken.None);
        _activeTaskId = execute.TaskId;

        var self = Self;
        var parent = Context.Parent;
        var ct = _executionCts.Token;
        var workDir = ResolveWorkingDirectory(execute.WorkspacePath);

        _ = Task.Run(async () =>
        {
            try
            {
                RuntimeAttemptResult? finalAttempt = null;
                for (var attemptNumber = 1; attemptNumber <= MaxExecutionAttempts; attemptNumber++)
                {
                    var prompt = attemptNumber == 1
                        ? execute.Prompt
                        : BuildRetryPrompt(execute);

                    var attempt = await RunAttemptAsync(prompt, execute, workDir, self, ct);
                    finalAttempt = attempt;

                    if (ShouldAcceptAttempt(execute, attempt))
                        break;

                    if (attemptNumber < MaxExecutionAttempts)
                    {
                        self.Tell(new RuntimeEvent(_agentId,
                            $"[WARN] Runtime returned an invalid structured result for task {execute.TaskId}. Retrying once with stricter instructions."));
                    }
                }

                if (finalAttempt is null)
                {
                    parent.Tell(new TaskFailed(execute.TaskId, "Runtime did not produce a task result", GraphId: execute.GraphId));
                    return;
                }

                PublishFinalResult(execute, finalAttempt, parent);
            }
            catch (OperationCanceledException)
            {
                parent.Tell(new TaskFailed(execute.TaskId, "Runtime execution cancelled", GraphId: execute.GraphId));
            }
            catch (Exception ex)
            {
                self.Tell(new RuntimeEvent(_agentId, $"[ERROR] Runtime failed: {ex.Message}"));
                parent.Tell(new TaskFailed(execute.TaskId, $"Runtime failed: {ex.Message}", GraphId: execute.GraphId));
            }
            finally
            {
                _runtime = null;
                _executionCts?.Dispose();
                _executionCts = null;
                _activeTaskId = null;
            }
        }, ct);
    }

    private async Task<RuntimeAttemptResult> RunAttemptAsync(
        string prompt,
        ExecuteTaskPrompt execute,
        string workDir,
        IActorRef self,
        CancellationToken ct)
    {
        var output = new StringBuilder();
        var runtimeConfig = _config.Runtimes.ResolveOrDefault(_runtimeId ?? _config.DefaultRuntimeId);
        await using var runtime = RuntimeFactory.Create(_agentId, runtimeConfig, _model, workDir, _config.RuntimeEnvironment);
        _runtime = runtime;

        await runtime.StartAsync(ct);
        await runtime.SendAsync(prompt, ct);

        await foreach (var line in runtime.ReadEventsAsync(ct))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            output.AppendLine(line);
            self.Tell(new RuntimeEvent(_agentId, line));
        }

        var parsed = StructuredTaskResultParser.Parse(output.ToString(), execute.TaskId);
        var artifacts = await WorkspaceArtifactCollector.CollectAsync(workDir, execute.TaskId, _agentId, ct);
        return new RuntimeAttemptResult(parsed, artifacts);
    }

    private void StartAdhocExecution(string prompt)
    {
        if (_executionCts is not null)
        {
            Context.Parent.Tell(new RuntimeEvent(_agentId, "[WARN] Ignoring ad hoc prompt while a task execution is active."));
            return;
        }

        var self = Self;
        var ct = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts?.Token ?? CancellationToken.None);
        _executionCts = ct;
        var workDir = ResolveWorkingDirectory(workspacePath: null);

        _ = Task.Run(async () =>
        {
            try
            {
                var runtimeConfig = _config.Runtimes.ResolveOrDefault(_runtimeId ?? _config.DefaultRuntimeId);
                await using var runtime = RuntimeFactory.Create(_agentId, runtimeConfig, _model, workDir, _config.RuntimeEnvironment);
                _runtime = runtime;

                await runtime.StartAsync(ct.Token);
                await runtime.SendAsync(prompt, ct.Token);

                await foreach (var line in runtime.ReadEventsAsync(ct.Token))
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        self.Tell(new RuntimeEvent(_agentId, line));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                self.Tell(new RuntimeEvent(_agentId, $"[ERROR] Runtime failed: {ex.Message}"));
            }
            finally
            {
                _runtime = null;
                _executionCts?.Dispose();
                _executionCts = null;
            }
        }, ct.Token);
    }

    private string ResolveWorkingDirectory(string? workspacePath)
    {
        if (!string.IsNullOrWhiteSpace(workspacePath))
            return workspacePath;

        if (!string.IsNullOrWhiteSpace(_config.RuntimeWorkingDirectory))
            return _config.RuntimeWorkingDirectory;

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static string BuildSummary(IReadOnlyList<ArtifactRef> artifacts)
    {
        if (artifacts.Count == 0)
            return "Task completed with no workspace changes.";

        var changedFiles = artifacts
            .Select(a => a.Metadata != null && a.Metadata.TryGetValue("relativePath", out var path) ? path : a.Uri)
            .Take(5)
            .ToArray();

        var suffix = artifacts.Count > changedFiles.Length ? "..." : string.Empty;
        return $"Task completed with {artifacts.Count} workspace change(s): {string.Join(", ", changedFiles)}{suffix}";
    }

    private static bool ShouldAcceptAttempt(ExecuteTaskPrompt execute, RuntimeAttemptResult attempt)
    {
        var parsed = attempt.Parsed;
        if (!parsed.HasEnvelope)
            return false;

        if (string.IsNullOrWhiteSpace(parsed.EnvelopeTaskId))
            return false;

        if (!string.Equals(parsed.EnvelopeTaskId, execute.TaskId, StringComparison.Ordinal))
        {
            return false;
        }

        if (parsed.Outcome == StructuredTaskResultParser.ParsedTaskOutcome.Failed)
            return true;

        if (parsed.Outcome == StructuredTaskResultParser.ParsedTaskOutcome.Decompose)
            return attempt.Parsed.Subplan is not null;

        if (parsed.Outcome != StructuredTaskResultParser.ParsedTaskOutcome.Completed)
            return false;

        if (parsed.ExpectedArtifactTypes.Count > 0 && attempt.Artifacts.Count == 0)
            return false;

        if (attempt.Artifacts.Count > 0)
            return true;

        return !string.IsNullOrWhiteSpace(parsed.Summary);
    }

    private void PublishFinalResult(ExecuteTaskPrompt execute, RuntimeAttemptResult attempt, IActorRef parent)
    {
        var parsed = attempt.Parsed;
        if (!parsed.HasEnvelope)
        {
            parent.Tell(new TaskFailed(
                execute.TaskId,
                "Runtime did not return the required structured result envelope.",
                GraphId: execute.GraphId));
            return;
        }

        if (string.IsNullOrWhiteSpace(parsed.EnvelopeTaskId))
        {
            parent.Tell(new TaskFailed(
                execute.TaskId,
                "Runtime returned a structured result without the required task_id.",
                GraphId: execute.GraphId));
            return;
        }

        if (!string.Equals(parsed.EnvelopeTaskId, execute.TaskId, StringComparison.Ordinal))
        {
            parent.Tell(new TaskFailed(
                execute.TaskId,
                $"Runtime returned a result for unexpected task id '{parsed.EnvelopeTaskId}'.",
                GraphId: execute.GraphId));
            return;
        }

        if (parsed.Outcome == StructuredTaskResultParser.ParsedTaskOutcome.Failed ||
            !string.IsNullOrWhiteSpace(parsed.FailureReason))
        {
            parent.Tell(new TaskFailed(
                execute.TaskId,
                parsed.FailureReason ?? parsed.Summary ?? "Runtime reported task failure.",
                GraphId: execute.GraphId));
            return;
        }

        if (parsed.Outcome == StructuredTaskResultParser.ParsedTaskOutcome.Decompose)
        {
            if (parsed.Subplan is null)
            {
                parent.Tell(new TaskFailed(
                    execute.TaskId,
                    "Runtime requested decomposition without a valid subplan.",
                    GraphId: execute.GraphId));
                return;
            }
        }
        else if (parsed.Outcome != StructuredTaskResultParser.ParsedTaskOutcome.Completed)
        {
            parent.Tell(new TaskFailed(
                execute.TaskId,
                "Runtime returned an unknown task outcome.",
                GraphId: execute.GraphId));
            return;
        }

        if (parsed.ExpectedArtifactTypes.Count > 0 && attempt.Artifacts.Count == 0)
        {
            parent.Tell(new TaskFailed(
                execute.TaskId,
                "Runtime declared expected artifacts but no workspace changes were detected.",
                GraphId: execute.GraphId));
            return;
        }

        if (parsed.Subplan is null && attempt.Artifacts.Count == 0 && string.IsNullOrWhiteSpace(parsed.Summary))
        {
            parent.Tell(new TaskFailed(
                execute.TaskId,
                "Runtime completed without artifacts, subplan, or a usable summary.",
                GraphId: execute.GraphId));
            return;
        }

        parent.Tell(new TaskCompleted(
            execute.TaskId,
            _agentId,
            Success: true,
            Summary: parsed.Summary ?? BuildSummary(attempt.Artifacts),
            GraphId: execute.GraphId,
            Artifacts: attempt.Artifacts,
            Subplan: parsed.Subplan));
    }

    private static string BuildRetryPrompt(ExecuteTaskPrompt execute)
    {
        return $"""
Your previous response did not satisfy the assigned task contract for task {execute.TaskId}.
Work only in the current git worktree for this task. Do not answer unrelated questions. Either execute the task exactly or return failure_reason.
Inspect any files you already changed. If they do not match the exact task, correct them before returning.
The final line of your response must be a <giant-isopod-result> envelope whose task_id is "{execute.TaskId}".

{execute.Prompt}
""";
    }

    protected override void PostStop()
    {
        _executionCts?.Cancel();
        _executionCts?.Dispose();
        _lifetimeCts?.Cancel();
        _lifetimeCts?.Dispose();
        _ = _runtime?.DisposeAsync();
        Context.Parent.Tell(new RuntimeExited(_agentId, 0));
    }
}

public record ExecuteTaskPrompt(string AgentId, string TaskId, string Prompt, string? GraphId = null, string? WorkspacePath = null);

/// <summary>Sets the token budget for the active task on this runtime actor.</summary>
public record SetTokenBudget(string TaskId, int MaxTokens);

internal sealed class TokenBudgetState(int maxTokens)
{
    public int MaxTokens { get; } = maxTokens;
    public long CumulativeChars { get; set; }
    public bool Warned { get; set; }
}

internal sealed record RuntimeAttemptResult(
    StructuredTaskResultParser.ParsedTaskResult Parsed,
    IReadOnlyList<ArtifactRef> Artifacts);
