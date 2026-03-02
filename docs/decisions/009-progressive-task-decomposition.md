# ADR-009: Progressive Task Decomposition

Date: 2026-03-02
Status: Proposed
Depends On: ADR-002, ADR-004, ADR-008

## Context

Giant-isopod's `TaskGraphActor` currently receives a fully-specified DAG upfront. All tasks
and dependencies are known at creation time. This works for pre-planned pipelines but fails
when:

1. **Scope is discovered during execution** — An agent starts a task and realizes it needs
   substeps A/B/C. Today it must either do everything inline (monolithic) or fail and let
   a human restructure the graph.
2. **Deep planning is wasteful** — Pre-decomposing to N depth burns tokens on branches that
   become irrelevant after the first few outputs. Early assumptions get baked into deep trees.
3. **Agents have local knowledge** — A specialist agent knows better than the orchestrator
   how to break down work in its domain. The orchestrator should set goals, not dictate steps.

The progressive deepening pattern from `docs/_inbox/discussion-001.md` proposes: decompose
one level, execute, then deepen only the branches that need it. Agents can propose sub-plans
as part of their result.

## Decision

Extend the task result protocol to allow agents to return a **ProposedSubplan** alongside
(or instead of) a deliverable. The `TaskGraphActor` evaluates the proposal, inserts approved
subtasks into the DAG, and the `DispatchActor` auctions them normally. Decomposition depth
emerges from execution, not from upfront planning.

## Design

### ProposedSubplan Model

```csharp
public record ProposedSubplan(
    string ParentTaskId,
    DecompositionReason Reason,
    IReadOnlyList<SubtaskProposal> Subtasks,
    StopCondition? StopWhen
);

public enum DecompositionReason
{
    TooLarge,            // task exceeds single-agent scope
    MissingInfo,         // need prerequisite data
    DependencyDiscovered,// found a blocking dependency
    Ambiguity,           // multiple valid approaches, need exploration
    ExternalToolRequired // needs a tool/resource the agent lacks
}

public record SubtaskProposal(
    string Description,
    IReadOnlySet<string> RequiredCapabilities,
    IReadOnlyList<string> DependsOnSubtasks,  // references within this proposal
    TimeSpan? BudgetCap,
    IReadOnlyList<ArtifactType>? ExpectedOutputTypes
);

public record StopCondition(
    StopKind Kind,
    string Description
);

public enum StopKind
{
    AllSubtasksComplete,   // default: done when all subtasks finish
    FirstSuccess,          // exploration: done when any subtask succeeds
    UserDecision           // escalate to human
}
```

### Extended TaskCompleted

```csharp
public record TaskCompleted(
    string TaskId,
    string Result,
    IReadOnlyList<ArtifactRef>? Artifacts,       // from ADR-008
    ProposedSubplan? Subplan                      // NEW
);
```

A result can have:
- Artifacts only → task is done, deliverables produced.
- Subplan only → task needs decomposition, no deliverable yet.
- Both → partial deliverable + remaining work proposed.

### Orchestrator Evaluation

`TaskGraphActor` does NOT blindly accept subplans. On receiving a `TaskCompleted` with a
subplan:

```
1. Validate proposal:
   - Subtask count within limit (default: max 10 per decomposition)
   - Total depth within limit (default: max 3 levels from root)
   - Budget caps within parent task's remaining budget
   - No circular dependencies within proposal

2. If valid:
   - Create new TaskNode for each subtask
   - Wire internal dependencies (DependsOnSubtasks → DAG edges)
   - Wire parent dependency (parent waits for all subtasks, or per StopCondition)
   - Parent task status → WaitingForSubtasks
   - New tasks enter frontier → DispatchActor auctions them

3. If invalid:
   - Reject: send TaskDecompositionRejected to parent task's agent
   - Agent can retry with a simpler proposal or complete the task as-is
```

### Messages

```csharp
// ── Decomposition ──
public record TaskDecompositionAccepted(string ParentTaskId, IReadOnlyList<string> SubtaskIds);
public record TaskDecompositionRejected(string ParentTaskId, string Reason);

// ── Parent completion ──
public record SubtasksCompleted(string ParentTaskId, IReadOnlyList<TaskCompleted> Results);
// Sent to parent's agent so it can synthesize a final result from subtask outputs.
```

### Task Lifecycle (Extended)

```
                         ┌──────────────┐
                         │   Pending     │
                         └──────┬───────┘
                                │ dispatched
                         ┌──────▼───────┐
                         │  InProgress   │
                         └──────┬───────┘
                           ┌────┴────┐
                      done │         │ subplan
                    ┌──────▼──┐  ┌───▼──────────────┐
                    │Completed│  │WaitingForSubtasks │
                    └─────────┘  └───────┬───────────┘
                                         │ all subtasks done
                                  ┌──────▼───────┐
                                  │  Synthesizing │ (agent merges results)
                                  └──────┬───────┘
                                  ┌──────▼───────┐
                                  │  Completed    │
                                  └──────────────┘
```

### Depth and Budget Guards

Progressive decomposition without limits leads to runaway trees. Guards:

| Guard | Default | Configurable |
|-------|---------|-------------|
| Max depth from root | 3 | Per task profile |
| Max subtasks per decomposition | 10 | Per task profile |
| Max total nodes in graph | 100 | Global |
| Budget inheritance | Subtask budgets sum ≤ parent remaining budget | Enforced |
| Decomposition timeout | Parent fails if subtasks not done in 2× parent budget | Enforced |

### Triggered Decomposition Heuristics

The orchestrator can also trigger decomposition proactively when:

- **High bid variance**: Bids for a task show high spread in fitness/cost → task is ambiguous,
  consider splitting.
- **Execution timeout**: Agent exceeds 50% of budget without progress → suggest decomposition.
- **Validator failure**: Task output fails validation → spawn a fix-it subtask rather than
  re-running the whole task.

## Trade-offs

### Why agent-proposed (not orchestrator-planned)

- Agents have domain context the orchestrator lacks. A code agent knows that "implement
  feature X" requires "write interface, implement adapter, add tests" better than a generic
  planner.
- Reduces orchestrator complexity — it evaluates proposals rather than generating plans.
- Naturally supports heterogeneous agent types (code, art, doc) without the orchestrator
  knowing domain-specific decomposition patterns.

### Why depth limits

- Unbounded recursion is the main risk. An agent that always proposes subplans creates
  infinite work. Depth cap of 3 means: root → tasks → subtasks → sub-subtasks, then stop.
- Budget inheritance ensures cost doesn't spiral — subtasks can't collectively cost more
  than their parent.

### Why not a separate planner agent

- Adding a dedicated planner actor adds indirection and latency. The pattern here uses the
  executing agent as a just-in-time planner, which is simpler.
- A planner agent can be layered later as a specialized bidder that only produces subplans
  (never deliverables). The protocol supports this without changes.

### Risk

- **Decomposition spam**: An agent that always proposes subplans to avoid doing work.
  Mitigate with: max decomposition count per agent per session, and require partial progress
  (artifacts) before accepting a subplan.
- **Synthesis complexity**: When subtasks complete, the parent agent must merge results.
  For simple cases this is concatenation. For complex cases (merging code from multiple
  branches) it may itself need decomposition. The depth limit prevents infinite recursion.
- **Ordering sensitivity**: Subtask proposals may conflict with existing frontier tasks.
  TaskGraphActor must check for overlap (same capabilities + description similarity) and
  deduplicate.

## References

- ADR-002: Task Graph (DAG) via ModernSatsuma (current DAG model)
- ADR-004: Market-First Coordination (auction integration)
- ADR-008: Artifact Registry (typed outputs in TaskCompleted)
- Discussion: `docs/_inbox/discussion-001.md` (progressive deepening, Sections 1–3)
- Current `TaskGraphActor`: `Plugin.Actors/TaskGraphActor.cs`
