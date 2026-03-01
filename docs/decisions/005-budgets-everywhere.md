# ADR-005: Budgets Everywhere (Time / Tokens / Risk)

Date: 2026-03-01
Status: Proposed
Depends On: ADR-002, ADR-004

## Context

Giant-isopod has no resource tracking or limits. An agent can run indefinitely, consume
unbounded tokens, and take on arbitrarily risky tasks. There is no way to:

- Set a deadline for a task or task graph.
- Track how many tokens an agent has consumed.
- Limit how many tasks an agent can run concurrently.
- Distinguish low-risk tasks (format a file) from high-risk ones (refactor a module).
- Abort a task that has exceeded its budget.

Swimming-tuna has timeouts (`RoleExecutionTimeoutSeconds`) and circuit breakers (3 failures
→ 5 min pause) but no token or cost accounting. CLIO has token estimation before API calls.
Overstory has `ov costs` for aggregate cost tracking. None have a unified budget model.

## Decision

Add three budget dimensions — **time**, **tokens**, and **risk** — as first-class fields on
task messages and enforce them in the actor layer.

## Design

### Budget Record

```csharp
public record TaskBudget(
    TimeSpan? Deadline = null,           // wall-clock time limit for this task
    int? MaxTokens = null,               // token output limit (approximate)
    RiskLevel Risk = RiskLevel.Normal    // risk classification
);

public enum RiskLevel
{
    Low,       // read-only operations, formatting, search
    Normal,    // standard code generation, file edits
    High,      // multi-file refactors, dependency changes
    Critical   // destructive operations, deployments
}
```

### Enhanced TaskRequest

```csharp
// Existing (unchanged for backward compat):
public record TaskRequest(string TaskId, string Description, IReadOnlySet<string> RequiredCapabilities);

// New overload with budget:
public record TaskRequestWithBudget(
    string TaskId,
    string Description,
    IReadOnlySet<string> RequiredCapabilities,
    TaskBudget Budget
) : TaskRequest(TaskId, Description, RequiredCapabilities);
```

The base `TaskRequest` continues to work with no budget (unlimited). `TaskRequestWithBudget`
adds budget constraints. Actors check `is TaskRequestWithBudget` to extract budget.

### Enhanced TaskNode (for DAG)

```csharp
// From ADR-002, extended:
public record TaskNode(
    string TaskId,
    string Description,
    IReadOnlySet<string> RequiredCapabilities,
    TaskBudget? Budget = null
);
```

### Time Budget Enforcement

**Where**: `AgentTaskActor` (per-agent task lifecycle tracker).

**How**: On `TaskAssigned`, if the originating request has a `Deadline`, schedule a
`ReceiveTimeout` or `Context.System.Scheduler.ScheduleTellOnce`:

```csharp
case TaskAssigned assigned when GetBudget(assigned) is { Deadline: { } deadline }:
    _activeTasks[assigned.TaskId] = new TaskState(assigned, DateTime.UtcNow);
    Context.System.Scheduler.ScheduleTellOnce(
        deadline, Self, new TaskTimedOut(assigned.TaskId), ActorRefs.NoSender);
    break;

case TaskTimedOut timedOut:
    _logger.LogWarning("Task {TaskId} exceeded deadline", timedOut.TaskId);
    // Kill the CLI process for this task
    Context.Parent.Tell(new TaskFailed(timedOut.TaskId, "Deadline exceeded"));
    break;
```

### Token Budget Tracking

**Where**: `AgentRpcActor` (CLI process output stream).

**How**: `CliAgentProcess` already streams stdout line-by-line. Count output characters
as a proxy for tokens (1 token ≈ 4 chars). When cumulative output exceeds `MaxTokens × 4`,
send a warning; at 120% of budget, kill the process.

```csharp
// Inside AgentRpcActor, on ProcessOutput
_cumulativeChars += output.Line.Length;
var estimatedTokens = _cumulativeChars / 4;

if (_budget?.MaxTokens is { } max)
{
    if (estimatedTokens > max * 1.2)
    {
        _logger.LogWarning("Task {TaskId} exceeded token budget ({Est}/{Max})",
            _taskId, estimatedTokens, max);
        _process.Kill();
    }
}
```

This is approximate. Exact token counting requires provider-specific parsing (different
for pi, kimi, codex, kilo). Start with the char-based proxy; refine per-provider later.

### Risk-Based Dispatch

**Where**: `DispatchActor` (bid selection) and `AgentActor` (bid evaluation).

**How**: Risk level affects dispatch in two ways:

1. **Bid filtering**: For `Critical` risk tasks, only agents with a minimum skill depth
   (e.g., >N successful completions of the required capability) may bid.
2. **Approval gate**: `Critical` risk tasks emit a `RiskApprovalRequired` message to the
   viewport bridge, requiring human confirmation before dispatch proceeds.

```csharp
public record RiskApprovalRequired(string TaskId, RiskLevel Risk, string Description);
public record RiskApproved(string TaskId);
public record RiskDenied(string TaskId, string Reason);
```

For `Low` and `Normal` risk, dispatch proceeds automatically. For `High`, a log warning is
emitted. For `Critical`, the task blocks until `RiskApproved` is received from the UI.

### Budget in TaskBid (Market Integration)

From ADR-004, `TaskBid` already includes `EstimatedDuration`. Extend it:

```csharp
public record TaskBid(
    string TaskId,
    string AgentId,
    double Fitness,
    int ActiveTaskCount,
    TimeSpan EstimatedDuration,
    int EstimatedTokens = 0       // agent's estimate of token cost
);
```

The dispatcher can now select agents that fit within the task's budget.

### Budget Reporting

```csharp
public record TaskBudgetReport(
    string TaskId,
    string AgentId,
    TimeSpan Elapsed,
    int EstimatedTokensUsed,
    RiskLevel Risk,
    bool DeadlineExceeded,
    bool TokenBudgetExceeded
);
```

Emitted by `AgentTaskActor` on task completion (success or failure). Published to
EventStream for telemetry collection.

### Graph-Level Budgets

A `SubmitTaskGraph` can include a graph-level budget that applies to the entire DAG:

```csharp
public record SubmitTaskGraph(
    string GraphId,
    IReadOnlyList<TaskNode> Nodes,
    IReadOnlyList<TaskEdge> Edges,
    TaskBudget? GraphBudget = null    // overall budget for the entire graph
);
```

`TaskGraphActor` enforces the graph-level deadline by aborting remaining nodes if the
total elapsed time exceeds the graph budget, even if individual nodes haven't timed out.

## Implementation Phases

### Phase 1: Time Budgets
- Add `TaskBudget` record and `TaskRequestWithBudget` message.
- Implement deadline enforcement in `AgentTaskActor`.
- Add `TaskTimedOut` handling.

### Phase 2: Token Tracking
- Add char-based token estimation in `AgentRpcActor`.
- Emit `TaskBudgetReport` on completion.
- Add token budget enforcement (kill on exceed).

### Phase 3: Risk Classification
- Add `RiskLevel` to task messages.
- Implement `RiskApprovalRequired` gate for `Critical` tasks.
- Integrate risk into bid filtering (ADR-004).

### Phase 4: Graph Budgets
- Add `GraphBudget` to `SubmitTaskGraph`.
- Implement graph-level deadline enforcement in `TaskGraphActor`.

## Trade-offs

### Why char-based token estimation

Exact token counting requires knowing the tokenizer for each provider (pi uses Claude,
kimi uses its own, etc.). A 4-chars-per-token approximation is good enough for budget
enforcement (we want "stop runaway tasks", not "bill to the cent"). Per-provider token
parsing can refine this later without changing the budget protocol.

### Why risk levels instead of a continuous score

Four discrete levels are easier to reason about and configure than a float. The mapping
from task description to risk level can start as manual (caller specifies) and later become
automatic (classify based on required capabilities: `shell_run` = High, `project_search` =
Low).

### Risk

- **False kills**: Char-based token estimation may kill a task that is within budget in
  real tokens. The 120% threshold provides margin. Monitor and adjust.
- **Deadline accuracy**: `ScheduleTellOnce` depends on Akka scheduler granularity (~100ms).
  Acceptable for budgets measured in seconds/minutes.

## References

- ADR-002: Task Graph (DAG) via ModernSatsuma
- ADR-004: Market-First Coordination
- Swimming-tuna `SupervisorActor` (circuit breaker, retry limits)
- Swimming-tuna `RuntimeOptions.RoleExecutionTimeoutSeconds` (time budget reference)
- CLIO `Memory/TokenEstimator.pm` (token estimation reference)
- Overstory `ov costs` command (cost tracking reference)
