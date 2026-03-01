# Handover: Swarm Enhancements (Sessions 1–7)

Date: 2026-03-01

## Session Summary

Sessions 1–6 built the full swarm agent framework: core actors, budget flow, DAG execution,
runtime/model separation, and the A2A/A2UI/AG-UI/GenUI protocol vertical slice.

Session 7 (housekeeping) merged all feature branches to main, adopted unify-build, evaluated
unify-ecs for future adoption, and verified the memory sidecar stack.

## Branch & PR State

### giant-isopod

All feature branches merged and deleted. Single `main` branch, pushed to remote.

| Branch | PR | Status |
|--------|----|--------|
| `worktree-swarm-enhancements` | #1 | **Merged** |
| `feat/risk-gate-and-memory` | #2 | **Merged** |
| `feat/taskid-collision-and-dag-viz` | #3 | **Merged** |
| `feat/stable-memory-embedding` | — | **Merged** (session 7, conflict resolution) |

No pending PRs. No open branches. Remote cleaned up.

### modern-satsuma

Packed as `Plate.ModernSatsuma 0.2.0-topological` in local NuGet feed.

## What Session 7 Contains

| Commit | Type | What |
|--------|------|------|
| `3aeda8a` | chore(godot) | Track .uid files for new scripts and scenes |
| `730cce1` | docs(ecs) | ADR-007: UnifyECS adoption plan (deferred) |
| `df73d83` | merge | Merge feat/stable-memory-embedding (conflict resolution) |
| `7ea84b9` | build(unify-build) | Adopt UnifyBuild 3.0.2 as local dotnet tool |

### Session 7 key changes

**Build tooling:**
- `.config/dotnet-tools.json` — UnifyBuild.Tool 3.0.2 pinned
- `.nuke/.gitkeep` — NUKE path discovery marker
- `build/build.config.json` — project groups: hosts (publish), plugins/contracts (compile)
- Taskfile `build`, `build:release`, `publish` tasks now use `dotnet unify-build`

**ADR-007 (docs/decisions/007-unify-ecs-adoption.md):**
- Evaluated `plate-projects/unify-ecs` for backend-agnostic ECS
- Decision: adopt when ECS surface grows (currently 9 components, 4 systems)
- Full migration guide with before/after code for components, systems, world management
- Spike plan: port MovementSystem first to validate

**Memory sidecar merge (feat/stable-memory-embedding):**
- Conflict resolution in MemvidActor (kept PipeTo async pattern, added ILogger)
- MemorySupervisorActor accepts ILoggerFactory (passes to child MemvidActors)
- Taskfile gains memory:install, memory:index, memory:search tasks

## Actor Tree

```
ActorSystem "agent-world"
├── /user/registry          ← SkillRegistryActor (capability index)
├── /user/memory            ← MemorySupervisorActor → MemvidActor (episodic, per-agent)
├── /user/knowledge         ← KnowledgeSupervisorActor → KnowledgeStoreActor (long-term, per-agent)
├── /user/blackboard        ← BlackboardActor (shared key-value, EventStream pub/sub)
├── /user/agents            ← AgentSupervisorActor (ForwardToAgent routing)
│   └── /user/agents/{id}   ← AgentActor (bidding, working memory, AG-UI adapter, task count)
│       ├── /rpc            ← AgentRuntimeActor (per-task token tracking, RuntimeFactory dispatch)
│       └── /tasks          ← AgentTaskActor (deadline enforcement, budget reports, GraphId tracking)
├── /user/dispatch          ← DispatchActor (market-first + risk gate, GraphId propagation)
├── /user/taskgraph         ← TaskGraphActor (ModernSatsuma DAG, wave dispatch, viewport notifications)
├── /user/viewport          ← ViewportActor (observer bridge to Godot, AG-UI events, task graph events)
└── /user/a2a               ← A2AActor (A2A task submission, status queries, agent card discovery)
```

## Memory Architecture (4 layers)

| Layer | Actor | Backend | Status |
|-------|-------|---------|--------|
| 1. Working | AgentActor (dict) | In-memory | Done |
| 2. Shared | BlackboardActor | EventStream pub/sub | Done |
| 3. Episodic | MemvidActor | memvid CLI → .mv2 files | Done |
| 4. Long-term | KnowledgeStoreActor | memory-sidecar → SQLite + sqlite-vec | Done (Phase 1) |

### Memory Sidecar (`memory-sidecar/`)

Python CLI with 5 commands: `index`, `search`, `embed`, `store`, `query`

**Installed dependencies:**
- `fastembed>=0.5` — ONNX embeddings (BAAI/bge-small-en-v1.5, 384 dims)
- `sqlite-vec>=0.1` — vector similarity search in SQLite
- `click>=8.1` — CLI framework

**Not yet added (future phases):**
- `cocoindex` — Tree-sitter-aware chunking (Phase 2, currently using simple splitter)
- `tree-sitter` — AST-aware code splitting (Phase 2)
- MCP server mode (Phase 3)
- QMD integration (Phase 3)

**C# integration:**
- `MemorySidecarClient` (Plugin.Process) — wraps CLI via CliWrap
- Methods: IndexCodebaseAsync, SearchCodeAsync, StoreKnowledgeAsync, SearchKnowledgeAsync
- `KnowledgeSupervisorActor` → `KnowledgeStoreActor` (per-agent, Layer 4)

**Taskfile tasks:**
- `task memory:install` — `uv pip install -e ".[dev]"`
- `task memory:index` — index codebase into SQLite
- `task memory:search -- "query"` — semantic search

## Next Session Priorities

### 1. Wire memory into agent workflow
- Connect KnowledgeStoreActor to AgentActor for automatic knowledge storage
- Add memory search as a pre-step before task execution (context retrieval)
- Index the project codebase: `task memory:index`

### 2. UI verification and polish
- F7 demo manual test — verify form renders, interactions work
- AG-UI event visibility — spawn agent, verify tool calls in console
- GenUIRenderer.tscn wiring into HUD layout
- TaskGraphView/Console overlap fix

### 3. Memory Phase 2
- Add `cocoindex` to pyproject.toml for Tree-sitter-aware chunking
- Replace simple splitter in `flows/codebase.py` with CocoIndex
- Incremental re-indexing support

### 4. Later priorities
- A2A integration testing
- AG-UI state events (StateSnapshot/StateDelta)
- GenUI surface lifecycle (update/delete from actors)
- Implement ApiAgentRuntime and SdkAgentRuntime (stubs)
- Persistence (GraphState to disk)
- GOAP planning → DAG → TaskGraphActor

## Key Design Decisions

- **Runtime/Model/Profile separation (ADR-006)**: IAgentRuntime (how), ModelSpec (which model),
  AieosEntity (who). RuntimeConfig [JsonPolymorphic] for Cli/Api/Sdk. RuntimeFactory creates
  correct IAgentRuntime. Model merging: SpawnAgent.Model overrides RuntimeConfig.DefaultModel.
- **UnifyBuild adoption**: `dotnet unify-build Compile` replaces raw `dotnet build`. Project
  groups: hosts (publish), plugins/contracts (compile). No NuGet packing needed for this project.
- **UnifyECS deferred (ADR-007)**: Current Friflo ECS footprint too small (9 components, 4 systems).
  Adopt when adding 3+ systems or needing backend benchmarking.
- **Memory sidecar as Python subprocess**: ONNX/FastEmbed can't load in Godot's .NET runtime.
  SQLite + sqlite-vec for vector storage (no Postgres, no Docker).
- **Market-first with fallback**: 500ms bid window, first-match fallback if no bids.
- **AG-UI as intermediate protocol**: AgUiAdapter converts RPC → AG-UI events → viewport → HUD.
- **A2UI flat adjacency model**: Components have ParentId, not nested children. Simpler to diff.
- **GenUI two-way binding**: RFC 6901 JSON Pointer for data model addressing.

## Key File Paths

| File | Role |
|------|------|
| `.config/dotnet-tools.json` | UnifyBuild 3.0.2 tool manifest |
| `build/build.config.json` | UnifyBuild project groups |
| `docs/decisions/006-runtime-model-profile.md` | ADR-006 |
| `docs/decisions/007-unify-ecs-adoption.md` | ADR-007 (deferred UnifyECS) |
| `memory-sidecar/` | Python memory sidecar package |
| `memory-sidecar/pyproject.toml` | Dependencies: fastembed, sqlite-vec, click |
| `memory-sidecar/src/memory_sidecar/cli.py` | CLI entry point (5 commands) |
| `memory-sidecar/src/memory_sidecar/flows/codebase.py` | Code indexing flow |
| `memory-sidecar/src/memory_sidecar/flows/knowledge.py` | Knowledge store/query flow |
| `project/plugins/Plugin.Process/MemorySidecarClient.cs` | C# wrapper for sidecar CLI |
| `project/plugins/Plugin.Actors/KnowledgeSupervisorActor.cs` | /user/knowledge supervisor |
| `project/plugins/Plugin.Actors/KnowledgeStoreActor.cs` | Per-agent knowledge actor |
| `project/contracts/Contracts.Core/Messages.cs` | All actor messages |
| `project/contracts/Contracts.Protocol/AgUi/AgUiEvents.cs` | 17 AG-UI event records |
| `project/contracts/Contracts.Protocol/A2A/A2ATypes.cs` | A2A spec types |
| `project/contracts/Contracts.Protocol/A2UI/A2UITypes.cs` | A2UI component types |
| `project/plugins/Plugin.Protocol/A2UIRenderer.cs` | A2UI JSON → GenUISurfaceSpec |
| `project/plugins/Plugin.Protocol/AgUiAdapter.cs` | RPC → AG-UI events |
| `project/plugins/Plugin.Actors/AgentWorldSystem.cs` | Actor tree bootstrap |
| `project/hosts/complete-app/Scenes/GenUI/GenUIRenderer.gd` | GDScript renderer |
| `project/hosts/complete-app/Scripts/Main.cs` | Godot host entry point |
