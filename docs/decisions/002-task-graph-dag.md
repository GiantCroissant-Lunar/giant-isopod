# ADR-002: Task Graph (DAG) via ModernSatsuma

Date: 2026-03-01
Status: Proposed
Depends On: ADR-001

## Context

Giant-isopod's current task dispatch is flat: `TaskRequest` → `DispatchActor` queries
`SkillRegistryActor` → picks first matching agent → `TaskAssigned`. There is no way to
express dependencies between tasks, decompose a task into subtasks, or enforce execution
order.

Real swarm workloads need structure:

- "Build module A, then B (which imports A), then run integration tests" is a DAG.
- "Index the codebase before running semantic search" is a dependency.
- "Plan → implement → review" is a sequential chain with fan-out potential.

Reference projects (Overstory, Persistent Swarm, swimming-tuna) all implement task
dependency tracking. Swimming-tuna uses GOAP for dynamic replanning; Overstory and
Persistent Swarm use explicit `blockedBy`/`blocks` edges. The simpler edge-based model
fits giant-isopod's current architecture better — GOAP can layer on later.

## Decision

Introduce a **Task Graph** backed by `Plate.ModernSatsuma.CustomGraph` (directed, acyclic)
with a new `TaskGraphActor` that owns the graph and coordinates dispatch of ready tasks.

### Why ModernSatsuma

`Plate.ModernSatsuma` is an existing plate-project graph library with:

- `CustomGraph` — mutable directed graph with `AddNode()`, `AddArc()`, `DeleteNode()`
- `Dfs` — cycle detection (enforce acyclicity on arc insertion)
- `Bfs` — level-order traversal for execution waves
- `Dijkstra` — critical path analysis with arc weights (task duration estimates)
- `Subgraph` adaptor — scoped views without copying
- Zero external dependencies, .NET Standard 2.1, already in the local NuGet feed pipeline

It needs one addition: a `TopologicalSort` algorithm (see modern-satsuma RFC-007).

### What ModernSatsuma does NOT provide

Task state, scheduling, dispatch, budgets, and async execution remain in giant-isopod's
actor layer. ModernSatsuma provides the structural graph; giant-isopod provides the runtime.

## Design

### New Messages (Contracts.Core)

```csharp
// ── Task graph ──

public record SubmitTaskGraph(string GraphId, IReadOnlyList<TaskNode> Nodes, IReadOnlyList<TaskEdge> Edges);
public record TaskNode(string TaskId, string Description, IReadOnlySet<string> RequiredCapabilities);
public record TaskEdge(string FromTaskId, string ToTaskId);  // FromTask must complete before ToTask starts

public record TaskGraphAccepted(string GraphId, int NodeCount, int EdgeCount);
public record TaskGraphRejected(string GraphId, string Reason);  // e.g., cycle detected

public record TaskReadyForDispatch(string GraphId, string TaskId, string Description, IReadOnlySet<string> RequiredCapabilities);
public record TaskNodeCompleted(string GraphId, string TaskId, bool Success, string? Summary = null);
public record TaskGraphCompleted(string GraphId, IReadOnlyDictionary<string, bool> Results);
```

### TaskGraphActor (`/user/taskgraph`)

```text
ActorSystem "agent-world"
├── /user/registry
├── /user/memory
├── /user/agents
├── /user/dispatch
├── /user/viewport
└── /user/taskgraph          ← NEW: owns CustomGraph, tracks node states
```

Responsibilities:

1. **Accept** `SubmitTaskGraph` — build `CustomGraph`, validate acyclicity via `Dfs`, reject
   if cycle detected.
2. **Compute ready set** — nodes whose incoming dependencies are all completed (in-degree
   zero in the remaining subgraph). Uses topological sort or BFS from roots.
3. **Dispatch ready tasks** — send `TaskReadyForDispatch` to `DispatchActor` for each ready
   node. Respects concurrency limit (configurable, default = agent count).
4. **Track completion** — on `TaskCompleted`, mark node done, recompute ready set, dispatch
   next wave.
5. **Handle failure** — on `TaskFailed`, mark node failed, propagate failure to all
   transitive dependents (cancel unreachable subgraph), report partial results.
6. **Report** — when all nodes are done (or failed), send `TaskGraphCompleted` to caller.

### Node State Machine

```text
Pending → Ready → Dispatched → Completed
                             → Failed → (dependents become Cancelled)
```

### State Tracking

```csharp
// Internal to TaskGraphActor — NOT a message
internal sealed class GraphState
{
    public CustomGraph Graph { get; }
    public Dictionary<string, Node> TaskToNode { get; }     // taskId → ModernSatsuma Node
    public Dictionary<long, TaskNode> NodeToTask { get; }    // Node.Id → TaskNode metadata
    public Dictionary<string, TaskNodeStatus> Status { get; } // taskId → Pending|Ready|Dispatched|Completed|Failed|Cancelled
}
```

### Integration with Existing DispatchActor

`TaskGraphActor` sends individual `TaskRequest` messages to `DispatchActor` — the existing
dispatch flow is unchanged. `TaskGraphActor` simply controls *when* each request is sent
based on dependency satisfaction.

```text
SubmitTaskGraph
    → TaskGraphActor validates DAG
    → computes ready set (roots)
    → sends TaskRequest per ready node → DispatchActor (existing)
    → DispatchActor assigns to agent (existing)
    → TaskCompleted flows back
    → TaskGraphActor recomputes ready set
    → dispatches next wave
    → ... until graph exhausted
```

### Critical Path (Optional, Phase 2)

With `Dijkstra` and task duration estimates as arc weights, `TaskGraphActor` can compute the
critical path and prioritize dispatching tasks on it. This is useful for time-budgeted graphs
(see ADR-005).

## Backward Compatibility

The existing `TaskRequest` → `DispatchActor` flow remains unchanged. Single tasks (no DAG)
continue to work exactly as before. `SubmitTaskGraph` is an additive new capability.

## Trade-offs

### Why an explicit DAG over GOAP

- DAGs are declarative and inspectable — you can visualize the graph in the viewport.
- GOAP requires defining world states and action preconditions, which adds complexity
  before we have enough experience to know the right action vocabulary.
- GOAP can layer on top later: a GOAP planner *produces* a DAG that `TaskGraphActor`
  executes. The two are complementary, not competing.

### Why ModernSatsuma over a hand-rolled adjacency list

- Already exists, tested, zero dependencies.
- Algorithms (DFS cycle detection, BFS traversal, Dijkstra critical path) come free.
- `Subgraph` adaptors enable scoped views without copying.
- Keeping graph structure in a dedicated library avoids coupling scheduling logic with
  graph mutation logic.

### Risk

- ModernSatsuma currently lacks `TopologicalSort` — addressed by RFC-007.
- `CustomGraph` is in-memory only. For durability across restarts, `TaskGraphActor` state
  would need serialization. This is acceptable for now (swarm sessions are ephemeral).

## References

- ADR-001: Skill-Based Tooling Over Actor-Per-Tool
- Plate.ModernSatsuma: internal repository `modern-satsuma` (see local NuGet feed pipeline docs)
- Modern-satsuma RFC-007: Topological Sort Enhancement
- swimming-tuna `TaskCoordinatorActor` + GOAP planner (reference implementation)
- Overstory `.overstory/state.json` task dependency model
- Persistent Swarm file-based task graph with `blockedBy`/`blocks`
