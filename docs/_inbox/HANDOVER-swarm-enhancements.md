# Handover: Swarm Enhancements Session

Date: 2026-03-01

## What Was Done

This session designed and implemented four agent swarm enhancements across two repos,
informed by analysis of `ref-projects/` (CLIO, Overstory, Persistent Swarm, Kimi CLI,
Pixel Agents) and `swimming-tuna`.

### Artifacts Created

#### RFCs / ADRs (design docs)

| Doc | Repo | Path |
|-----|------|------|
| ADR-002 Task Graph (DAG) | giant-isopod | `docs/decisions/002-task-graph-dag.md` |
| ADR-003 Three-Layer Memory | giant-isopod | `docs/decisions/003-three-layer-memory.md` |
| ADR-004 Market-First Coordination | giant-isopod | `docs/decisions/004-market-first-coordination.md` |
| ADR-005 Budgets Everywhere | giant-isopod | `docs/decisions/005-budgets-everywhere.md` |
| RFC-007 Topological Sort | modern-satsuma | `docs/rfcs/RFC-007-topological-sort.md` |

#### Code (all builds clean, 0 warnings, 0 errors)

**modern-satsuma** — `feat/topological-sort` branch (2 commits ahead of main):

| File | What |
|------|------|
| `Traversal/TopologicalSort.cs` | Kahn's algorithm: `Order`, `Layers`, `IsAcyclic`, `CyclicNodes` |
| `Extensions/Builders.cs` | `TopologicalSortBuilder` + `IGraph.TopologicalSort()` extension |
| `Tests/TopologicalSortTests.cs` | 10 tests (linear, diamond, parallel, cycle, partial, fan-out, deep chain) |

All 135 tests pass.

**giant-isopod** — `worktree-swarm-enhancements` branch (3 commits ahead of main):

| File | What |
|------|------|
| `Contracts.Core/Messages.cs` | 30+ new message types: task graph, blackboard, budgets, market, working memory |
| `Plugin.Actors/TaskGraphActor.cs` | NEW: `/user/taskgraph` — DAG validation, dependency dispatch, failure propagation |
| `Plugin.Actors/BlackboardActor.cs` | NEW: `/user/blackboard` — key-value + EventStream pub/sub (stigmergy) |
| `Plugin.Actors/DispatchActor.cs` | REWRITTEN: market-first bid-collect-select cycle with orchestrator fallback |
| `Plugin.Actors/AgentActor.cs` | MODIFIED: bid evaluation, working memory, active task count tracking |
| `Plugin.Actors/AgentSupervisorActor.cs` | MODIFIED: `ForwardToAgent` routing, `ILoggerFactory` passthrough |
| `Plugin.Actors/AgentTaskActor.cs` | REWRITTEN: deadline enforcement via `IWithTimers`, budget reporting |
| `Plugin.Actors/AgentRpcActor.cs` | MODIFIED: char-based token tracking, `SetTokenBudget` message |
| `Plugin.Actors/AgentWorldSystem.cs` | MODIFIED: `Blackboard` + `TaskGraph` actor refs, wired into bootstrap |

## Branch State

### giant-isopod

```
main: d0f10e6  (has the ADR docs committed on main)
worktree: worktree-swarm-enhancements @ 363ce3f  (3 commits: contracts + actors + market/budget)
worktree path: C:\lunar-horse\yokan-projects\giant-isopod\.claude\worktrees\swarm-enhancements
```

The worktree has uncommitted changes in `docs/_inbox/` (this handover doc). The ADR docs
were committed to `main` before the worktree was created, so the worktree branch already
has them.

To merge: `git checkout main && git merge worktree-swarm-enhancements`

### modern-satsuma

```
main: c393c89  (before RFC-007 and TopologicalSort)
branch: feat/topological-sort @ 32c7e06  (2 commits: RFC doc + implementation)
```

To merge: `git checkout main && git merge feat/topological-sort`

**Note**: modern-satsuma main has pre-existing unstaged changes (a prior refactor that
reorganized files into subdirectories). The `feat/topological-sort` branch only touched
3 files and doesn't conflict with those changes, but be aware they exist.

## Actor Tree (Updated)

```
ActorSystem "agent-world"
├── /user/registry          ← SkillRegistryActor (unchanged)
├── /user/memory            ← MemorySupervisorActor → MemvidActor per agent (unchanged)
├── /user/blackboard         ← NEW: BlackboardActor (shared key-value, EventStream pub/sub)
├── /user/agents            ← AgentSupervisorActor (added ForwardToAgent routing)
│   └── /user/agents/{id}   ← AgentActor (added bidding, working memory, task count)
│       ├── /rpc            ← AgentRpcActor (added token tracking)
│       └── /tasks          ← AgentTaskActor (added deadline enforcement, budget reports)
├── /user/dispatch          ← DispatchActor (rewritten: market-first with fallback)
├── /user/taskgraph          ← NEW: TaskGraphActor (DAG validation, wave dispatch)
└── /user/viewport          ← ViewportActor (unchanged)
```

## What's NOT Done Yet (Future Work)

### Near-term (completed in this branch)

1. ~~**Budget flow through assignment**~~ — DONE. `TaskAssigned` now carries optional
   `Budget`. Flows from `TaskGraphActor` → `DispatchActor` → `AgentActor` → `AgentTaskActor`.

2. ~~**Token budget wiring**~~ — DONE. `AgentActor` sends `SetTokenBudget` to `/rpc`
   when `TaskAssigned` has `MaxTokens`. Per-task tracking via dictionary.

3. **Risk approval gate** — Messages defined. Implementation in `feat/risk-gate-and-memory`
   branch (PR #2). `DispatchActor` pauses Critical-risk tasks for viewport approval.

4. **Memvid CliWrap integration** — `MemvidActor` is still a stub. Needs actual CliWrap
   calls to `memvid put` and `memvid search`. Note: ADR-003 revised to treat Memvid as
   episodic (per-task) memory, not long-term storage.

5. ~~**ModernSatsuma integration**~~ — DONE. `TaskGraphActor` uses
   `Plate.ModernSatsuma.TopologicalSort` (RFC-007) for cycle detection. Packed as
   `Plate.ModernSatsuma 0.2.0-topological` in local NuGet feed.

### Medium-term

6. **Agent-to-agent communication** — A2A skill is defined in ADR-001 but not implemented.
   Could route through `BlackboardActor` signals or direct actor messaging.

7. **Fitness refinements** — Current fitness is simple capability overlap ratio. Enhance
   with specialization depth (success history per capability), memory relevance, recent
   performance.

8. ~~**Graph-level timeout handling**~~ — DONE. `HandleGraphTimedOut` cancels all
   pending/dispatched nodes and emits `TaskGraphCompleted`. Deadline timer cancelled
   on normal graph completion.

9. **Persistence** — Task graph state is in-memory only. For session recovery, serialize
   `GraphState` to disk or use ArcadeDB (like swimming-tuna).

10. **Viewport integration** — Publish task graph state changes and bid/award events to
    `IViewportBridge` so the Godot UI can visualize DAG progress and market activity.

### Low-priority

11. **Per-provider token parsing** — Replace char-based approximation (1 token ≈ 4 chars)
    with provider-specific parsing for pi, kimi, codex, kilo output formats.

12. **Consensus voting** — Swimming-tuna's `ConsensusActor` pattern for multi-agent
    approval decisions. Not needed until agents produce conflicting outputs.

13. **GOAP planning** — Layer GOAP on top of the task graph: a GOAP planner produces
    a DAG that `TaskGraphActor` executes. Requires defining world states and action
    vocabularies first.

## Key Design Decisions

- **Market-first with fallback**: 500ms bid window, first-match fallback if no bids.
  Agents self-select based on fitness and capacity. See ADR-004.

- **Blackboard over direct messaging**: Cross-agent coordination uses EventStream pub/sub
  (stigmergy), not direct actor-to-actor messages. This decouples agents. See ADR-003.

- **ModernSatsuma for DAG validation**: `TaskGraphActor` uses
  `Plate.ModernSatsuma.TopologicalSort` (RFC-007) for cycle detection. The adjacency
  lists are still maintained for runtime dispatch (incoming/outgoing edges). See ADR-002.

- **ILoggerFactory passthrough**: Changed `AgentActor` constructor from `ILogger<AgentActor>`
  to `ILoggerFactory` so child actors (`AgentTaskActor`) can create their own typed loggers.
  `AgentSupervisorActor` was updated accordingly.

- **ForwardToAgent message**: New routing message for `AgentSupervisorActor` to forward
  arbitrary payloads (like `TaskAvailable`) to specific agents by ID. Used by market
  dispatch to broadcast bid requests.
