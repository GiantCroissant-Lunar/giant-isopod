# Handover: Swarm Enhancements (Sessions 1–6)

Date: 2026-03-01

## Session Summary

Sessions 1–2 implemented core swarm actors, budget flow, ModernSatsuma DAG, risk gate,
and ADR-003 memory architecture. PRs #1 and #2 merged to main.

Session 3 fixed cross-graph TaskId collisions (review comment #7), added viewport DAG
visualization pipeline, GodotXterm fallback terminal, and TaskGraphView (GraphEdit).

Session 4 resolved all four session-3 handover priorities: F5 demo graph trigger,
user:// recording paths, GodotXterm native libs, and MemvidActor CliWrap integration.

Session 5 implemented the Runtime/Model/Profile separation (ADR-006): renamed
IAgentProcess→IAgentRuntime, introduced ModelSpec, RuntimeConfig hierarchy, RuntimeRegistry,
RuntimeFactory, and runtimes.json.

Session 6 implemented the full A2A/A2UI/AG-UI/GenUI vertical slice: protocol contract
types, enhanced A2UIRenderer, GDScript GenUI renderer with data binding, AG-UI adapter
and event wiring, and A2AActor for agent-to-agent protocol.

## Branch & PR State

### giant-isopod

| Branch | Base | PR | Commits | Status |
|--------|------|----|---------|--------|
| `worktree-swarm-enhancements` | `main` | #1 | 10 | **Merged** |
| `feat/risk-gate-and-memory` | `main` | #2 | 3 | **Merged** |
| `feat/taskid-collision-and-dag-viz` | `main` | #3 | 16 | **Merged** |
| `feat/taskid-collision-and-dag-viz` | — | #4 | 3 (session 5) | **Pending** |
| `feat/taskid-collision-and-dag-viz` | — | — | 6 (session 6) | **Not yet PR'd** |

Worktree path:
- `C:\lunar-horse\yokan-projects\giant-isopod\.claude\worktrees\swarm-enhancements`

### modern-satsuma

Packed as `Plate.ModernSatsuma 0.2.0-topological` in local NuGet feed.

## What Session 6 Contains (6 commits)

| Commit | Type | What |
|--------|------|------|
| `ad3ec72` | feat(contracts) | AG-UI event records, A2A types, A2UI types, new actor messages, extend viewport bridge |
| `3159f3c` | feat(a2ui) | Enhanced A2UIRenderer: all 4 message types, flat GenUISurfaceSpec, RFC 6901 data model |
| `c6d9ef5` | feat(genui) | GDScript GenUIRenderer (7 component types, data binding, action signal), F7 demo, HUD wiring |
| `b2717d0` | feat(agui) | AgUiAdapter: instance-based with per-agent state, full AG-UI event mapping |
| `85d1f58` | feat(agui) | Wire AG-UI events: AgentActor → ViewportActor → HudController console display |
| `ab95470` | feat(a2a) | A2AActor at /user/a2a: task submission, status queries, agent card discovery |

### Session 6 key changes

**Protocol contracts (Contracts.Protocol):**
- `AgUi/AgUiEvents.cs` — 17 AG-UI event records: RunStarted/Finished/Error, StepStarted/Finished, TextMessage Start/Content/End, ToolCall Start/Args/End/Result, StateSnapshot/Delta, MessagesSnapshot, Raw, Custom
- `A2A/A2ATypes.cs` — Google A2A spec: AgentCard, A2ATask, A2AMessage, polymorphic A2APart (Text/File/Data), A2AArtifact
- `A2UI/A2UITypes.cs` — A2UIComponent (flat with ParentId), A2UIAction, GenUISurfaceSpec

**Core messages (Contracts.Core/Messages.cs):**
- `GenUIAction(AgentId, SurfaceId, ActionId, ComponentId, Payload?)` — UI → agent
- `AgUiEvent(AgentId, Event)` — agent → viewport
- `A2ASendTask`, `A2AGetTask`, `A2ATaskResult`, `QueryAgentCards`, `AgentCardsResult`, `AgentCardInfo`

**Viewport bridge:**
- `IViewportBridge.PublishAgUiEvent()` — new default-noop method
- `GodotViewportBridge` — `AgUiViewportEvent`, `GenUIActionEvent` records, `OnGenUIAction` callback

**A2UIRenderer (Plugin.Protocol):**
- Parses all 4 A2UI message types: createSurface, updateComponents, updateDataModel, deleteSurface
- Outputs flat `GenUISurfaceSpec` with per-component `ParentId` adjacency (not nested children)
- RFC 6901 JSON Pointer data model resolution
- Backward-compatible `Parse()` method for legacy callers

**AgUiAdapter (Plugin.Protocol):**
- Converted from static to instance class, tracks run/step/tool state per agent
- `MapRpcEventToAgUiEvents(line)` → `List<object>` of AG-UI events
- First event → RunStarted; tool_use → ToolCallStart; tool_result → ToolCallEnd; text → TextMessageContent; exit → RunFinished
- Static `MapRpcEventToActivity()` kept for backward compatibility

**GenUIRenderer.gd (GDScript):**
- Component catalog: label→Label, button→Button, text_input→LineEdit, container→VBox/HBoxContainer, progress→ProgressBar, checkbox→CheckBox, separator→HSeparator
- `render_a2ui(agent_id, a2ui_json)` — called from C#
- Flat adjacency rendering (components reference ParentId)
- RFC 6901 JSON Pointer data binding (read + apply)
- Two-way binding on text_input (text_changed) and checkbox (toggled)
- `action_triggered` signal → HudController → Main → actor system

**Wiring:**
- AgentActor: new `AgUiAdapter` field, emits AG-UI events in RuntimeEvent handler
- ViewportActor: handles `AgUiEvent` → bridge
- HudController: handles `AgUiViewportEvent` (logs tool names, streaming text to console), connects `action_triggered` signal from GenUIHost, emits `OnGenUIActionTriggered`
- Main.cs: subscribes `OnGenUIActionTriggered`, forwards `GenUIAction` to agent supervisor; F7 keybind loads `demo-a2ui-form.json` and sends through viewport bridge
- Plugin.Actors.csproj: added `Plugin.Protocol` project reference

**A2AActor (Plugin.Actors):**
- Handles `A2ASendTask` → creates internal task, forwards `TaskRequest` to `/user/dispatch`
- Handles `A2AGetTask` → returns task status JSON
- Handles `QueryAgentCards` → async `Ask` to registry, `PipeTo` sender
- Subscribes EventStream for `TaskCompleted`/`TaskFailed` → updates internal task state
- Wired into AgentWorldSystem at `/user/a2a`

## Actor Tree

```
ActorSystem "agent-world"
├── /user/registry          ← SkillRegistryActor (capability index)
├── /user/memory            ← MemorySupervisorActor → MemvidActor (episodic, per-task)
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

### Next session: verification and polish

1. **F7 demo manual test** — Run app, press F7, verify form renders in GenUIHost. Type in text input, toggle checkbox, click Submit — verify action logged to console and forwarded to actor system.

2. **AG-UI event visibility** — Spawn agent with real runtime, verify tool call names appear in console panel via AG-UI events.

3. **GenUIRenderer.tscn wiring** — The GenUIRenderer scene needs to be instantiated in the HUD layout (currently GenUIHost expects `render_a2ui` method on an existing node). May need to add GenUIRenderer.tscn as child of GenUIHost in SwarmhudView.

4. **TaskGraphView / Console overlap** — TaskGraphView has fixed offsets (top: 80, bottom: 400) that collide with the console panel (350px, anchored bottom).

5. **TaskGraphView visibility** — graph view is always rendered even when no graph is active; should be hidden by default and shown only when a graph is submitted.

### Later priorities

1. A2A integration testing (send task from one agent to another via A2AActor)
2. AG-UI state events (StateSnapshot/StateDelta — not yet emitted by adapter)
3. GenUI surface lifecycle (updateComponents, updateDataModel, deleteSurface — renderer supports them but no actor produces them yet)
4. Agent card enrichment (populate from AIEOS profile skills, not just agentId)
5. Fitness refinements (specialization depth, performance history)
6. Persistence (serialize GraphState to disk or embedded DB)
7. Long-term knowledge store (SQLite/LiteDB, KnowledgeStoreActor)
8. Per-provider token parsing (replace char-based approximation)
9. Consensus voting (multi-agent approval)
10. GOAP planning (planner → DAG → TaskGraphActor)
11. Task-scoped `.mv2` files (ADR-003 calls for per-task, current is per-agent)
12. Implement ApiAgentRuntime and SdkAgentRuntime (currently stubs)

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
- **AG-UI as intermediate protocol**: AgUiAdapter converts raw RPC output into structured
  AG-UI events (lifecycle, text, tool call). Events flow AgentActor → ViewportActor →
  GodotViewportBridge → HudController. Decouples agent output format from UI rendering.
- **A2UI flat adjacency model**: A2UIRenderer converts nested component trees to flat list
  with ParentId references. GenUIRenderer.gd rebuilds tree on Godot side. Simpler to
  serialize and diff than nested trees.
- **GenUI two-way binding**: text_input and checkbox components write back to data model
  via signal callbacks. RFC 6901 JSON Pointer for addressing model fields.
- **A2AActor EventStream subscription**: Subscribes to TaskCompleted/TaskFailed globally
  to track task state without coupling to individual agent actors. Uses Ask+PipeTo for
  async registry queries.

## Key File Paths

| File | Role |
|------|------|
| `project/contracts/Contracts.Core/Messages.cs` | All actor messages incl. GenUIAction, AgUiEvent, A2A messages |
| `project/contracts/Contracts.Core/IAgentRuntime.cs` | Agent runtime interface |
| `project/contracts/Contracts.Core/ModelSpec.cs` | Provider-agnostic model specification |
| `project/contracts/Contracts.Core/IViewportBridge.cs` | PublishAgUiEvent + task graph methods |
| `project/contracts/Contracts.Core/IMemoryStore.cs` | IMemoryStore interface + MemoryHit record |
| `project/contracts/Contracts.Protocol/AgUi/AgUiEvents.cs` | 17 AG-UI event records |
| `project/contracts/Contracts.Protocol/A2A/A2ATypes.cs` | Google A2A spec types |
| `project/contracts/Contracts.Protocol/A2UI/A2UITypes.cs` | A2UI component/action/surface types |
| `project/contracts/Contracts.Protocol/Runtime/` | RuntimeConfig hierarchy |
| `project/plugins/Plugin.Protocol/A2UIRenderer.cs` | A2UI JSON → flat GenUISurfaceSpec |
| `project/plugins/Plugin.Protocol/AgUiAdapter.cs` | RPC events → AG-UI events (instance, stateful) |
| `project/plugins/Plugin.Process/RuntimeRegistry.cs` | Loads runtimes.json or legacy |
| `project/plugins/Plugin.Process/RuntimeFactory.cs` | Creates IAgentRuntime from RuntimeConfig |
| `project/plugins/Plugin.Actors/AgentActor.cs` | AgUiAdapter field, AG-UI event emission |
| `project/plugins/Plugin.Actors/ViewportActor.cs` | Handles AgUiEvent → bridge |
| `project/plugins/Plugin.Actors/A2AActor.cs` | A2A protocol: task submission, status, agent cards |
| `project/plugins/Plugin.Actors/AgentWorldSystem.cs` | Creates /user/a2a, exposes A2A property |
| `project/plugins/Plugin.Actors/TaskGraphActor.cs` | DAG validation + wave dispatch |
| `project/plugins/Plugin.Actors/DispatchActor.cs` | Market bidding + risk gate |
| `project/hosts/complete-app/Scenes/GenUI/GenUIRenderer.gd` | GDScript component renderer with data binding |
| `project/hosts/complete-app/Scenes/GenUI/GenUIRenderer.tscn` | GenUI scene |
| `project/hosts/complete-app/Data/Demo/demo-a2ui-form.json` | Demo A2UI form (F7) |
| `project/hosts/complete-app/Scripts/GodotViewportBridge.cs` | AgUiViewportEvent, GenUIActionEvent, OnGenUIAction |
| `project/hosts/complete-app/Scripts/HudController.cs` | AG-UI console display, GenUI action signal wiring |
| `project/hosts/complete-app/Scripts/Main.cs` | F7 demo, GenUIAction forwarding, runtime loading |
| `project/hosts/complete-app/Scripts/TaskGraphView.cs` | GraphEdit DAG visualization |
| `docs/decisions/006-runtime-model-profile.md` | ADR-006 |
