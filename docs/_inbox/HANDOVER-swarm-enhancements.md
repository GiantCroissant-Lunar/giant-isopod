# Handover: Swarm Enhancements (Sessions 1–4)

Date: 2026-03-01

## Session Summary

Sessions 1–2 implemented core swarm actors, budget flow, ModernSatsuma DAG, risk gate,
and ADR-003 memory architecture. PRs #1 and #2 merged to main.

Session 3 fixed cross-graph TaskId collisions (review comment #7), added viewport DAG
visualization pipeline, GodotXterm fallback terminal, and TaskGraphView (GraphEdit).

Session 4 resolved all four session-3 handover priorities: F5 demo graph trigger,
user:// recording paths, GodotXterm native libs, and MemvidActor CliWrap integration.

## Branch & PR State

### giant-isopod

| Branch | Base | PR | Commits | Status |
|--------|------|----|---------|--------|
| `worktree-swarm-enhancements` | `main` | #1 | 10 | **Merged** |
| `feat/risk-gate-and-memory` | `main` | #2 | 3 | **Merged** |
| `feat/taskid-collision-and-dag-viz` | `main` | #3 | 13 | Sessions 3–4, PR pending |

Worktree path:
- `C:\lunar-horse\yokan-projects\giant-isopod\.claude\worktrees\swarm-enhancements`

### modern-satsuma

Packed as `Plate.ModernSatsuma 0.2.0-topological` in local NuGet feed.

## What Session 4 Contains (5 new commits)

| Commit | Type | What |
|--------|------|------|
| `8cfbd4a` | feat | F5 keyboard shortcut submits demo 6-node DAG through full pipeline |
| `20acc56` | fix | Console log + asciicast recordings use Godot `user://` instead of `%USERPROFILE%` |
| `1619dc6` | feat | MemvidActor wired to MemvidClient via CliWrap (PipeTo async pattern) |
| `dc13ba7` | chore | Track TaskGraphView.cs.uid |

### GodotXterm native libs (local-only, not committed)

Downloaded [godot-xterm v4.0.3](https://github.com/lihop/godot-xterm/releases/tag/v4.0.3)
native binaries into `addons/godot_xterm/lib/`. The `.gitignore` in `lib/` excludes them
from git. New clones need to re-download — see setup instructions below.

## What Session 3 Contains (8 commits)

| Commit | Type | What |
|--------|------|------|
| `926affd` | fix | Add `GraphId` field to TaskRequest, TaskAssigned, TaskCompleted, TaskFailed |
| `9c3991c` | fix | Route completions to TaskGraphActor with GraphId; TryFindGraph O(1) lookup |
| `7237df7` | feat | Notify* messages + IViewportBridge task graph methods (default no-op) |
| `0fead8d` | feat | TaskGraphActor emits viewport notifications; Viewport created before TaskGraph |
| `fa77c98` | feat | TaskGraphView: GraphEdit DAG with topological layout + status colors |
| `8e29751` | fix | Move TaskGraphView to HUD CanvasLayer; add RichTextLabel terminal fallback |
| `c35ad03` | fix | ClassDB-based GodotXterm detection (insufficient — class registered without lib) |
| `7192fb6` | fix | Probe Terminal.HasMethod("write") for reliable native lib detection |

## Actor Tree

```
ActorSystem "agent-world"
├── /user/registry          ← SkillRegistryActor (capability index)
├── /user/memory            ← MemorySupervisorActor → MemvidActor (episodic, per-task)
├── /user/blackboard        ← BlackboardActor (shared key-value, EventStream pub/sub)
├── /user/agents            ← AgentSupervisorActor (ForwardToAgent routing)
│   └── /user/agents/{id}   ← AgentActor (bidding, working memory, task count)
│       ├── /rpc            ← AgentRpcActor (per-task token tracking)
│       └── /tasks          ← AgentTaskActor (deadline enforcement, budget reports, GraphId tracking)
├── /user/dispatch          ← DispatchActor (market-first + risk gate, GraphId propagation)
├── /user/taskgraph         ← TaskGraphActor (ModernSatsuma DAG, wave dispatch, viewport notifications)
└── /user/viewport          ← ViewportActor (observer bridge to Godot, task graph events)
```

## Setup: GodotXterm Native Libs

The native binaries are git-ignored. To set up on a new machine:

```bash
curl -L -o /tmp/godot-xterm.zip https://github.com/lihop/godot-xterm/releases/download/v4.0.3/godot-xterm-v4.0.3.zip
unzip /tmp/godot-xterm.zip "addons/godot_xterm/lib/*" -d /tmp/gxt
cp /tmp/gxt/addons/godot_xterm/lib/*.dll project/hosts/complete-app/addons/godot_xterm/lib/
# On Linux: cp /tmp/gxt/addons/godot_xterm/lib/*.so project/hosts/complete-app/addons/godot_xterm/lib/
rm -rf /tmp/godot-xterm.zip /tmp/gxt
```

Without native libs, the app still runs — HudController falls back to RichTextLabel console.

## Remaining Work (Prioritized)

### Next priorities

1. Agent-to-agent communication (via blackboard or direct messaging)
2. Fitness refinements (specialization depth, performance history)
3. Persistence (serialize GraphState to disk or embedded DB)
4. Long-term knowledge store (SQLite/LiteDB, KnowledgeStoreActor)
5. Per-provider token parsing (replace char-based approximation)
6. Consensus voting (multi-agent approval)
7. GOAP planning (planner → DAG → TaskGraphActor)
8. Task-scoped `.mv2` files (ADR-003 calls for per-task, current is per-agent)

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
- **GraphId threading (session 3)**: All task lifecycle messages carry optional GraphId.
  TaskGraphActor.TryFindGraph does O(1) direct lookup when GraphId is present, with
  fallback scan for backward compatibility. Fixes cross-graph TaskId collision.
- **Viewport DAG pipeline**: TaskGraphActor → NotifyTaskGraphSubmitted/NotifyTaskNodeStatusChanged
  → ViewportActor → IViewportBridge (default no-op) → GodotViewportBridge event queue →
  Main._Process() drain → TaskGraphView (GraphEdit with topological layer layout).
- **GodotXterm fallback**: HudController probes Terminal.HasMethod("write") after scene
  instantiation. If native lib is missing, falls back to RichTextLabel-based console.
  ClassDB.ClassExists("Terminal") is unreliable (class registered from .gdextension metadata
  even when DLL is absent).
- **MemvidActor CliWrap (session 4)**: MemvidActor delegates to MemvidClient (CliWrap 3.8.2).
  Uses Akka PipeTo pattern for async bridging. MemvidExecutable config flows from
  AgentWorldConfig → MemorySupervisorActor → MemvidActor constructor.

## Key File Paths

| File | Role |
|------|------|
| `project/contracts/Contracts.Core/Messages.cs` | GraphId on task messages + Notify* records |
| `project/contracts/Contracts.Core/IViewportBridge.cs` | 3 default no-op task graph methods |
| `project/contracts/Contracts.Core/IMemoryStore.cs` | IMemoryStore interface + MemoryHit record |
| `project/plugins/Plugin.Actors/TaskGraphActor.cs` | TryFindGraph, viewport notifications |
| `project/plugins/Plugin.Actors/AgentTaskActor.cs` | GraphId storage + enrichment |
| `project/plugins/Plugin.Actors/AgentActor.cs` | Routes graph-tagged completions to /user/taskgraph |
| `project/plugins/Plugin.Actors/DispatchActor.cs` | GraphId through bid/approval pipeline |
| `project/plugins/Plugin.Actors/ViewportActor.cs` | Handles Notify* + TaskGraphCompleted |
| `project/plugins/Plugin.Actors/AgentWorldSystem.cs` | Viewport created before TaskGraph; config wiring |
| `project/plugins/Plugin.Actors/MemvidActor.cs` | CliWrap-backed episodic memory (PipeTo async) |
| `project/plugins/Plugin.Actors/MemorySupervisorActor.cs` | Per-agent MemvidActor supervisor |
| `project/plugins/Plugin.Process/MemvidClient.cs` | CliWrap memvid CLI wrapper |
| `project/hosts/complete-app/Scripts/GodotViewportBridge.cs` | 3 task graph event records |
| `project/hosts/complete-app/Scripts/TaskGraphView.cs` | GraphEdit DAG visualization |
| `project/hosts/complete-app/Scripts/HudController.cs` | Fallback terminal + user:// paths |
| `project/hosts/complete-app/Scripts/Main.cs` | TaskGraphView + F5 demo graph + drain loop |
