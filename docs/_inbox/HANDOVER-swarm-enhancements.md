# Handover: Swarm Enhancements (Sessions 1–5)

Date: 2026-03-01

## Session Summary

Sessions 1–2 implemented core swarm actors, budget flow, ModernSatsuma DAG, risk gate,
and ADR-003 memory architecture. PRs #1 and #2 merged to main.

Session 3 fixed cross-graph TaskId collisions (review comment #7), added viewport DAG
visualization pipeline, GodotXterm fallback terminal, and TaskGraphView (GraphEdit).

Session 4 resolved all four session-3 handover priorities: F5 demo graph trigger,
user:// recording paths, GodotXterm native libs, and MemvidActor CliWrap integration.
Also addressed two rounds of PR #3 review comments (Dispose→node flags, IsCanceled,
TaskRunId correlation, graph overlap fix).

Session 5 implemented the Runtime/Model/Profile separation (ADR-006): renamed
IAgentProcess→IAgentRuntime, CliAgentProcess→CliAgentRuntime, AgentRpcActor→AgentRuntimeActor,
introduced ModelSpec, RuntimeConfig hierarchy (Cli/Api/Sdk), RuntimeRegistry, RuntimeFactory,
and runtimes.json with polymorphic type discriminators.

## Branch & PR State

### giant-isopod

| Branch | Base | PR | Commits | Status |
|--------|------|----|---------|--------|
| `worktree-swarm-enhancements` | `main` | #1 | 10 | **Merged** |
| `feat/risk-gate-and-memory` | `main` | #2 | 3 | **Merged** |
| `feat/taskid-collision-and-dag-viz` | `main` | #3 | 16 | **Merged** |
| `feat/taskid-collision-and-dag-viz` | — | #4 | 3 (session 5) | **Pending** |

Worktree path:
- `C:\lunar-horse\yokan-projects\giant-isopod\.claude\worktrees\swarm-enhancements`

### modern-satsuma

Packed as `Plate.ModernSatsuma 0.2.0-topological` in local NuGet feed.

## What Session 5 Contains (3 commits)

| Commit | Type | What |
|--------|------|------|
| `a02b041` | refactor | Introduce IAgentRuntime, ModelSpec, RuntimeConfig hierarchy, rename Process→Runtime messages |
| `03e56fe` | refactor | CliAgentRuntime, RuntimeRegistry, RuntimeFactory, AgentRuntimeActor; delete old files |
| `75e3a17` | refactor | runtimes.json with polymorphic discriminator, legacy fallback, ADR-006 |

### Key changes in session 5

- **IAgentRuntime** replaces IAgentProcess (same interface, renamed)
- **ModelSpec** record: `(Provider?, ModelId?, Parameters?)` — first-class model specification
- **RuntimeConfig** hierarchy with `[JsonPolymorphic]`:
  - `CliRuntimeConfig` — CLI subprocess (executable, args, env, defaults)
  - `ApiRuntimeConfig` — stub (BaseUrl, ApiKeyEnvVar)
  - `SdkRuntimeConfig` — stub (SdkName, Options)
- **RuntimeRegistry**: `LoadFromJson` (new format) + `LoadFromLegacyCliProviders` (old format)
- **RuntimeFactory**: pattern-matches RuntimeConfig → IAgentRuntime; `MergeModel()` for null-coalescing
- **AgentRuntimeActor** replaces AgentRpcActor (uses RuntimeFactory.Create)
- **AgentWorldConfig**: `Runtimes`, `DefaultRuntimeId`, `RuntimeWorkingDirectory`, `RuntimeEnvironment`
- **runtimes.json**: new config format with `"type": "cli"` discriminator and `defaultModel`
- **SpawnAgent**: `RuntimeId` (was CliProviderId), `Model` (new ModelSpec field)
- Messages renamed: StartProcess→StartRuntime, ProcessStarted→RuntimeStarted, etc.
- Viewport events renamed: ProcessStartedEvent→RuntimeStartedEvent, etc.
- HUD: `SetRuntimes()`, `_activeRuntimes`, counter shows "Runtimes: N"

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
│       ├── /rpc            ← AgentRuntimeActor (per-task token tracking, RuntimeFactory dispatch)
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

### Next session: UI layout and polish

1. **TaskGraphView / Console overlap** — TaskGraphView has fixed offsets (top: 80, bottom: 400)
   that collide with the console panel (350px, anchored bottom). Need to either constrain the
   graph view to avoid the console area, or make the console push it up when visible.

2. **TaskGraphView visibility** — graph view is always rendered even when no graph is active;
   should be hidden by default and shown only when a graph is submitted.

3. **Asciicast recording path** — verify recordings directory creation under `user://recordings`.

### Later priorities

1. Agent-to-agent communication (via blackboard or direct messaging)
2. Fitness refinements (specialization depth, performance history)
3. Persistence (serialize GraphState to disk or embedded DB)
4. Long-term knowledge store (SQLite/LiteDB, KnowledgeStoreActor)
5. Per-provider token parsing (replace char-based approximation)
6. Consensus voting (multi-agent approval)
7. GOAP planning (planner → DAG → TaskGraphActor)
8. Task-scoped `.mv2` files (ADR-003 calls for per-task, current is per-agent)
9. Implement ApiAgentRuntime and SdkAgentRuntime (currently stubs)

## Key Design Decisions

- **Runtime/Model/Profile separation (ADR-006)**: Three orthogonal concerns — how the agent
  executes (IAgentRuntime), which model powers it (ModelSpec), who the agent is (AieosEntity).
  RuntimeConfig uses [JsonPolymorphic] for Cli/Api/Sdk dispatch. RuntimeFactory creates the
  correct IAgentRuntime. Model merging: explicit SpawnAgent.Model overrides RuntimeConfig.DefaultModel.
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
  to /rpc. Per-task token tracking via dictionary in AgentRuntimeActor.
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
| `project/contracts/Contracts.Core/Messages.cs` | Runtime messages, GraphId on task messages, Notify* records |
| `project/contracts/Contracts.Core/IAgentRuntime.cs` | Agent runtime interface (replaces IAgentProcess) |
| `project/contracts/Contracts.Core/ModelSpec.cs` | Provider-agnostic model specification |
| `project/contracts/Contracts.Core/IViewportBridge.cs` | PublishRuntime* + task graph methods |
| `project/contracts/Contracts.Core/IMemoryStore.cs` | IMemoryStore interface + MemoryHit record |
| `project/contracts/Contracts.Protocol/Runtime/` | RuntimeConfig, CliRuntimeConfig, Api/SdkRuntimeConfig, RuntimesRoot |
| `project/plugins/Plugin.Process/CliAgentRuntime.cs` | CLI runtime via CliWrap (replaces CliAgentProcess) |
| `project/plugins/Plugin.Process/RuntimeRegistry.cs` | Loads runtimes.json or legacy cli-providers.json |
| `project/plugins/Plugin.Process/RuntimeFactory.cs` | Creates IAgentRuntime from RuntimeConfig + ModelSpec |
| `project/plugins/Plugin.Actors/AgentRuntimeActor.cs` | Runtime pipe actor (replaces AgentRpcActor) |
| `project/plugins/Plugin.Actors/TaskGraphActor.cs` | TryFindGraph, viewport notifications |
| `project/plugins/Plugin.Actors/AgentTaskActor.cs` | GraphId storage + enrichment |
| `project/plugins/Plugin.Actors/AgentActor.cs` | Runtime lifecycle, bidding, working memory |
| `project/plugins/Plugin.Actors/DispatchActor.cs` | GraphId through bid/approval pipeline |
| `project/plugins/Plugin.Actors/ViewportActor.cs` | Handles Runtime*/Notify*/TaskGraphCompleted |
| `project/plugins/Plugin.Actors/AgentWorldSystem.cs` | AgentWorldConfig with Runtimes field |
| `project/plugins/Plugin.Actors/MemvidActor.cs` | CliWrap-backed episodic memory (PipeTo async) |
| `project/plugins/Plugin.Process/MemvidClient.cs` | CliWrap memvid CLI wrapper |
| `project/hosts/complete-app/Data/Runtimes/runtimes.json` | New polymorphic runtime config |
| `project/hosts/complete-app/Scripts/GodotViewportBridge.cs` | Runtime + task graph event records |
| `project/hosts/complete-app/Scripts/TaskGraphView.cs` | GraphEdit DAG visualization |
| `project/hosts/complete-app/Scripts/HudController.cs` | SetRuntimes, fallback terminal, user:// paths |
| `project/hosts/complete-app/Scripts/Main.cs` | runtimes.json loading, TaskGraphView, F5 demo, drain loop |
| `docs/decisions/006-runtime-model-profile.md` | ADR-006: Runtime/Model/Profile separation |
