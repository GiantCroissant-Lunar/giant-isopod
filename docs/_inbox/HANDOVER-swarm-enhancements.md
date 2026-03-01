# Handover: Swarm Enhancements (Sessions 1–2)

Date: 2026-03-01

## Session Summary

Two sessions implemented swarm enhancements across two repos. Session 1 designed ADRs
and built the core actors. Session 2 wired budget flow, integrated ModernSatsuma,
added GraphTimedOut handling, addressed 18 Copilot review comments, created the risk
approval gate + revised ADR-003 memory architecture in a stacked PR.

## Branch & PR State

### giant-isopod

| Branch | Base | PR | Commits | Status |
|--------|------|----|---------|--------|
| `worktree-swarm-enhancements` | `main` | [#1](https://github.com/GiantCroissant-Lunar/giant-isopod/pull/1) | 10 | Review fixes pushed, ready to merge |
| `feat/risk-gate-and-memory` | `worktree-swarm-enhancements` | [#2](https://github.com/GiantCroissant-Lunar/giant-isopod/pull/2) | 3 | Stacked on PR #1, merge after #1 |

Worktree paths:
- PR #1: `C:\lunar-horse\yokan-projects\giant-isopod\.claude\worktrees\swarm-enhancements`
- PR #2: `C:\lunar-horse\yokan-projects\giant-isopod\.claude\worktrees\risk-and-memory`

### modern-satsuma

```
main: c393c89  (has pre-existing unstaged refactor changes)
branch: feat/topological-sort @ 32c7e06  (2 commits: RFC-007 + TopologicalSort impl)
```

Packed as `Plate.ModernSatsuma 0.2.0-topological` in local NuGet feed.
To merge when ready: `git -C /c/lunar-horse/plate-projects/modern-satsuma checkout main && git merge feat/topological-sort`

## Merge Order

```
1. gh pr merge 1 --merge    (worktree-swarm-enhancements → main)
2. gh pr merge 2 --merge    (feat/risk-gate-and-memory → main, after #1 lands)
3. Optionally merge modern-satsuma feat/topological-sort → main
```

## What PR #1 Contains (10 commits)

| Commit | Type | What |
|--------|------|------|
| `12b44ed` | feat | 30+ new message types: task graph, blackboard, budgets, market, working memory |
| `28ea0ee` | feat | TaskGraphActor + BlackboardActor, wired into bootstrap |
| `363ce3f` | feat | Market-first dispatch + budget enforcement in DispatchActor |
| `c7a02d7` | docs | Session handover doc |
| `eda41e8` | feat | Budget flow end-to-end: TaskAssigned carries Budget, SetTokenBudget wired |
| `e919b05` | feat | GraphTimedOut handler in TaskGraphActor |
| `6d963f8` | feat | ModernSatsuma TopologicalSort integration |
| `eef5957` | fix | Review: crash bugs (dict mutation, duplicate TaskId), Success flag, timer cancel |
| `0f33a0a` | fix | Review: Sender routing, bid validation, Watch on subscribe, per-task token tracking |
| `dc1a1ab` | docs | Handover doc updated |

## What PR #2 Contains (3 commits, stacked on #1)

| Commit | Type | What |
|--------|------|------|
| `f18d0b8` | docs | Cherry-picked ADR docs (002–005) into branch |
| `6b12f01` | docs | ADR-003 revised: four-layer memory (working → shared → episodic → long-term) |
| `2dfdd61` | feat | Risk approval gate in DispatchActor (Critical tasks paused for viewport approval) |

## Copilot Review — 18 Comments on PR #1

All 18 addressed. Summary of fixes:

**Crash bugs**: Dict mutation during iteration (#13), duplicate TaskId crash (#6)
**Logic bugs**: Sender misrouting in DispatchActor (#1/#12), Success flag ignored (#14), missing Watch (#8)
**Design**: Per-task token tracking (#3), bid validation (#16)
**Cleanup**: Unused using (#2), null-coalesce (#9), misleading comment (#10), unused var (#15), timer cancel (#17)
**Docs**: Handover updated (#4/#5/#11/#18)

**Not addressed** (review comment #7): Cross-graph TaskId collision. TaskIds can collide
across active graphs. Low risk for now (graph submitters control IDs), but worth prefixing
with GraphId in a future pass.

## Actor Tree

```
ActorSystem "agent-world"
├── /user/registry          ← SkillRegistryActor (capability index)
├── /user/memory            ← MemorySupervisorActor → MemvidActor (episodic, per-task)
├── /user/blackboard        ← BlackboardActor (shared key-value, EventStream pub/sub)
├── /user/agents            ← AgentSupervisorActor (ForwardToAgent routing)
│   └── /user/agents/{id}   ← AgentActor (bidding, working memory, task count)
│       ├── /rpc            ← AgentRpcActor (per-task token tracking)
│       └── /tasks          ← AgentTaskActor (deadline enforcement, budget reports)
├── /user/dispatch          ← DispatchActor (market-first + risk gate)
├── /user/taskgraph         ← TaskGraphActor (ModernSatsuma DAG, wave dispatch)
└── /user/viewport          ← ViewportActor (observer bridge to Godot)
```

## Memory Architecture (Revised ADR-003)

| Layer | Actor | Scope | Lifetime | Implementation |
|-------|-------|-------|----------|----------------|
| Working | `AgentActor` dict | Per-agent | Per-task | Done |
| Shared | `/user/blackboard` | Cross-agent | Session | Done |
| Episodic | `/user/memory/{id}` | Per-agent, per-task | Task run | Stub (needs CliWrap) |
| Long-term | `/user/knowledge/{id}` | Per-agent | Persistent | Future (embedded DB) |

## Remaining Work (Prioritized)

### Next session priorities

1. **Address review comment #7** — prefix TaskIds with GraphId or validate uniqueness
   across active graphs in TaskGraphActor.
2. **Memvid CliWrap integration** — wire MemvidActor to actual `memvid put`/`search`
   CLI calls. Scope per-task (episodic memory).
3. **Viewport integration** — publish task graph state + market events to IViewportBridge
   so Godot UI can visualize DAG progress.

### Later

4. Agent-to-agent communication (via blackboard or direct messaging)
5. Fitness refinements (specialization depth, performance history)
6. Persistence (serialize GraphState to disk or embedded DB)
7. Long-term knowledge store (SQLite/LiteDB, KnowledgeStoreActor)
8. Per-provider token parsing (replace char-based approximation)
9. Consensus voting (multi-agent approval)
10. GOAP planning (planner → DAG → TaskGraphActor)

## Key Design Decisions

- **Market-first with fallback**: 500ms bid window, first-match fallback if no bids.
  Agents self-select based on fitness and capacity. See ADR-004.
- **Risk gate**: Critical-risk tasks paused for viewport approval before assignment.
- **Blackboard (stigmergy)**: Cross-agent coordination via EventStream pub/sub, not
  direct actor messages. Subscribers auto-cleaned via Watch/Terminated. See ADR-003.
- **ModernSatsuma for DAG**: TopologicalSort (RFC-007) for cycle detection. Adjacency
  lists maintained alongside for runtime dispatch.
- **Four-layer memory**: Working (dict) → Shared (blackboard) → Episodic (memvid per-task)
  → Long-term (embedded DB, future). See revised ADR-003.
- **Budget end-to-end**: TaskAssigned carries Budget. AgentActor wires SetTokenBudget
  to /rpc. Per-task token tracking via dictionary in AgentRpcActor.
