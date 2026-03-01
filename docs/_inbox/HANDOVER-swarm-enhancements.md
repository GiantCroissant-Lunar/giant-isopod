# Handover: Swarm Enhancements (Sessions 1–3)

Date: 2026-03-01

## Session Summary

Sessions 1–2 implemented core swarm actors, budget flow, ModernSatsuma DAG, risk gate,
and ADR-003 memory architecture. PRs #1 and #2 merged to main.

Session 3 fixed cross-graph TaskId collisions (review comment #7), added viewport DAG
visualization pipeline, GodotXterm fallback terminal, and TaskGraphView (GraphEdit).

## Branch & PR State

### giant-isopod

| Branch | Base | PR | Commits | Status |
|--------|------|----|---------|--------|
| `worktree-swarm-enhancements` | `main` | #1 | 10 | **Merged** |
| `feat/risk-gate-and-memory` | `main` | #2 | 3 | **Merged** |
| `feat/taskid-collision-and-dag-viz` | `main` | — | 8 | Session 3 work, pending PR |

Worktree path (session 3):
- `C:\lunar-horse\yokan-projects\giant-isopod\.claude\worktrees\swarm-enhancements`

### modern-satsuma

Packed as `Plate.ModernSatsuma 0.2.0-topological` in local NuGet feed.

## What Session 3 Contains (8 commits on `feat/taskid-collision-and-dag-viz`)

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

## Remaining Work (Prioritized)

### Next session priorities

1. **TaskGraphView not visible** — TaskGraphView is wired and renders in the HUD
   CanvasLayer, but it is hidden by default (`Visible = false`) and only shows when
   a `SubmitTaskGraph` message is received. There is currently **no UI to submit a
   task graph**. Options:
   - Add a "Submit Test Graph" button to HUD that sends a sample SubmitTaskGraph
   - Wire a keyboard shortcut (e.g. F5) to submit a demo DAG
   - Wait until a real orchestrator/planner submits graphs programmatically

2. **Asciicast recordings in user home** — `.cast` files are written to
   `%USERPROFILE%\giant-isopod-recordings\` (hardcoded in HudController line 44).
   Should be moved to `user://recordings/` (Godot user data dir) or made
   configurable via AgentWorldConfig.

3. **GodotXterm native libs missing** — `addons/godot_xterm/lib/` is empty.
   The RichTextLabel fallback works, but for proper terminal rendering, download
   or build the GodotXterm native binaries for Windows x86_64 and place them in
   `project/hosts/complete-app/addons/godot_xterm/lib/`.

4. **Memvid CliWrap integration** — wire MemvidActor to actual `memvid put`/`search`
   CLI calls. Scope per-task (episodic memory).

### Later

5. Agent-to-agent communication (via blackboard or direct messaging)
6. Fitness refinements (specialization depth, performance history)
7. Persistence (serialize GraphState to disk or embedded DB)
8. Long-term knowledge store (SQLite/LiteDB, KnowledgeStoreActor)
9. Per-provider token parsing (replace char-based approximation)
10. Consensus voting (multi-agent approval)
11. GOAP planning (planner → DAG → TaskGraphActor)

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

## Key File Paths (Session 3)

| File | Role |
|------|------|
| `project/contracts/Contracts.Core/Messages.cs` | GraphId on task messages + Notify* records |
| `project/contracts/Contracts.Core/IViewportBridge.cs` | 3 default no-op task graph methods |
| `project/plugins/Plugin.Actors/TaskGraphActor.cs` | TryFindGraph, viewport notifications |
| `project/plugins/Plugin.Actors/AgentTaskActor.cs` | GraphId storage + enrichment |
| `project/plugins/Plugin.Actors/AgentActor.cs` | Routes graph-tagged completions to /user/taskgraph |
| `project/plugins/Plugin.Actors/DispatchActor.cs` | GraphId through bid/approval pipeline |
| `project/plugins/Plugin.Actors/ViewportActor.cs` | Handles Notify* + TaskGraphCompleted |
| `project/plugins/Plugin.Actors/AgentWorldSystem.cs` | Viewport created before TaskGraph |
| `project/hosts/complete-app/Scripts/GodotViewportBridge.cs` | 3 new event records |
| `project/hosts/complete-app/Scripts/TaskGraphView.cs` | **New** — GraphEdit DAG visualization |
| `project/hosts/complete-app/Scripts/HudController.cs` | Fallback terminal + WriteToTerminal |
| `project/hosts/complete-app/Scripts/Main.cs` | TaskGraphView in HUDRoot + drain loop |
