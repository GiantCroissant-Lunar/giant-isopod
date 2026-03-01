using Akka.Actor;
using GiantIsopod.Contracts.Core;
using Microsoft.Extensions.Logging;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// /user/agents/{name}/tasks — tracks active task lifecycle for an agent.
/// Enforces time budgets via timers and emits TaskBudgetReport on completion.
/// </summary>
public sealed class AgentTaskActor : UntypedActor, IWithTimers
{
    private readonly string _agentId;
    private readonly ILogger<AgentTaskActor> _logger;
    private readonly Dictionary<string, TaskState> _activeTasks = new();

    public ITimerScheduler Timers { get; set; } = null!;

    public AgentTaskActor(string agentId, ILogger<AgentTaskActor> logger)
    {
        _agentId = agentId;
        _logger = logger;
    }

    protected override void OnReceive(object message)
    {
        switch (message)
        {
            case TaskAssigned task:
                var budget = task.Budget;
                var state = new TaskState(task.TaskId, DateTimeOffset.UtcNow, budget);
                _activeTasks[task.TaskId] = state;

                // Start deadline timer if budget specifies one
                if (budget?.Deadline is { } deadline)
                {
                    Timers.StartSingleTimer(
                        $"deadline-{task.TaskId}",
                        new TaskTimedOut(task.TaskId),
                        deadline);
                    _logger.LogDebug("Task {TaskId} deadline set: {Deadline}", task.TaskId, deadline);
                }
                break;

            case TaskCompleted completed:
                if (_activeTasks.Remove(completed.TaskId, out var completedState))
                {
                    Timers.Cancel($"deadline-{completed.TaskId}");
                    EmitBudgetReport(completedState, false);
                }
                Context.Parent.Tell(completed);
                break;

            case TaskFailed failed:
                if (_activeTasks.Remove(failed.TaskId, out var failedState))
                {
                    Timers.Cancel($"deadline-{failed.TaskId}");
                    EmitBudgetReport(failedState, false);
                }
                Context.Parent.Tell(failed);
                break;

            case TaskTimedOut timedOut:
                if (_activeTasks.Remove(timedOut.TaskId, out var timedOutState))
                {
                    _logger.LogWarning("Task {TaskId} exceeded deadline for agent {AgentId}",
                        timedOut.TaskId, _agentId);
                    EmitBudgetReport(timedOutState, true);
                    Context.Parent.Tell(new TaskFailed(timedOut.TaskId, "Deadline exceeded"));
                }
                break;

            case TokenBudgetExceeded exceeded:
                if (_activeTasks.ContainsKey(exceeded.TaskId))
                {
                    _logger.LogWarning("Task {TaskId} exceeded token budget ({Used}/{Max}) for agent {AgentId}",
                        exceeded.TaskId, exceeded.EstimatedTokens, exceeded.MaxTokens, _agentId);
                }
                break;
        }
    }

    private void EmitBudgetReport(TaskState state, bool deadlineExceeded)
    {
        var elapsed = DateTimeOffset.UtcNow - state.StartedAt;
        var risk = state.Budget?.Risk ?? RiskLevel.Normal;

        var report = new TaskBudgetReport(
            state.TaskId,
            _agentId,
            elapsed,
            EstimatedTokensUsed: 0, // filled by RpcActor tracking
            risk,
            deadlineExceeded,
            TokenBudgetExceeded: false);

        Context.System.EventStream.Publish(report);
    }

    private record TaskState(string TaskId, DateTimeOffset StartedAt, TaskBudget? Budget);
}

/// <summary>Sent by AgentRpcActor when token output exceeds budget.</summary>
public record TokenBudgetExceeded(string TaskId, int EstimatedTokens, int MaxTokens);
