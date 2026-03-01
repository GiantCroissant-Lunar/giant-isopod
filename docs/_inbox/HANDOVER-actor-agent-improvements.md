# Actor & Agent Improvements — Handover

## Branch
`feat/actor-agent-improvements` via worktree at `.claude/worktrees/actor-agent-improvements/`

## Scope
Improve the actor/agent system in `project/plugins/Plugin.Actors/`. The memory-sidecar
enhancements are handled on a separate branch — this work focuses on the actor hierarchy,
agent lifecycle, task orchestration, and runtime resilience.

## Current State of Our Actors

| Actor | Path | Role |
|---|---|---|
| AgentWorldSystem | AgentWorldSystem.cs | Bootstraps Akka actor tree (registry, memory, knowledge, blackboard, agents, dispatch, taskgraph, viewport, a2a) |
| AgentActor | AgentActor.cs | Per-agent: owns runtime, skills, memory ref, task bidding, knowledge retrieval, demo cycle |
| AgentSupervisorActor | AgentSupervisorActor.cs | Supervises all agents, restart strategy (3/min) |
| AgentTaskActor | AgentTaskActor.cs | Per-agent task lifecycle, deadline timers, budget reports |
| DispatchActor | DispatchActor.cs | Market-first bidding with fallback, risk approval gate |
| TaskGraphActor | TaskGraphActor.cs | DAG execution: submit graph → dispatch ready nodes → track completion |
| BlackboardActor | BlackboardActor.cs | Shared key-value signals, pub/sub (stigmergy) |
| ViewportActor | ViewportActor.cs | Observer bridge to Godot ECS |
| SkillRegistryActor | SkillRegistryActor.cs | Capability registry |
| MemorySupervisorActor | MemorySupervisorActor.cs | Episodic memory supervision |
| KnowledgeSupervisorActor | KnowledgeSupervisorActor.cs | Semantic knowledge via sidecar |
| A2AActor | A2AActor.cs | Agent-to-agent protocol |

## Improvement Areas (Prioritized)

### 1. Agent Lifecycle & Checkpoint (from overstory)
**Gap:** Our agents have no session persistence. If an agent crashes or the system restarts,
all in-flight context is lost. There's no handoff mechanism between sessions.

**Borrow from:** `ref-projects/overstory/src/agents/lifecycle.ts`, `checkpoint.ts`, `identity.ts`

**What to build:**
- `AgentCheckpointActor` or checkpoint behavior within `AgentActor` — periodically saves
  agent state (active tasks, working memory, progress) to disk
- Handoff protocol: when an agent session ends, it writes a checkpoint; on restart, it
  resumes from checkpoint
- Agent identity tracking: sessions completed, expertise domains, recent tasks (like
  overstory's `identity.yaml`)
- Store checkpoints under `{AgentDataPath}/{agentId}/checkpoint.json`

### 2. Worker State Machine & Context Compaction (from spacebot)
**Gap:** Our `AgentActor` has a simple connected/disconnected state. No formal state machine,
no context window management, no compaction when conversations get long.

**Borrow from:** `ref-projects/spacebot/src/agent/worker.rs`, `compactor.rs`

**What to build:**
- Formal state machine for agent runtime: `Starting → Running → WaitingForInput → Done → Failed`
  with validated transitions (like spacebot's `WorkerState`)
- Context compaction: monitor estimated token usage, trigger background compaction when
  approaching limits (background at 70%, aggressive at 85%, emergency truncate at 95%)
- Segment-based execution: run in segments of N turns, check context between segments
- Max-segments safety valve to prevent unbounded loops

### 3. Status Block / Live Status (from spacebot)
**Gap:** No centralized live status snapshot. The viewport gets individual events but there's
no aggregated status block that can be injected into agent context.

**Borrow from:** `ref-projects/spacebot/src/agent/status.rs`

**What to build:**
- `AgentStatusActor` or extend `ViewportActor` with a `StatusBlock` that tracks:
  - Active tasks per agent (with tool call counts)
  - Recently completed items
  - Active A2A conversations
- Render method that produces a text block injectable into agent system prompts
- Agents get ambient awareness of what other agents are doing

### 4. Cortex / Bulletin System (from spacebot)
**Gap:** No periodic memory consolidation or bulletin generation. Agents query knowledge
on-demand but don't have a curated ambient context.

**Borrow from:** `ref-projects/spacebot/src/agent/cortex.rs`

**What to build:**
- `CortexActor` that periodically generates a "memory bulletin" — an LLM-curated summary
  of current knowledge, recent events, active goals
- Bulletin sections: identity/core facts, recent memories, decisions, high-importance context,
  preferences, active goals, observations
- Inject bulletin into agent system prompts so all agents have ambient awareness
- Signal observation: cortex watches EventStream for memory saves, task completions,
  compaction events

### 5. Structured Orchestration Patterns (from overstory + persistent-swarm)
**Gap:** Our dispatch is flat — any agent can bid on any task. No hierarchical coordination,
no lead/scout/builder specialization, no structured decomposition.

**Borrow from:** `ref-projects/overstory/agents/coordinator.md`, `lead.md`, `scout.md`, `builder.md`
and `ref-projects/persistent-swarm/.github/agents/orchestrator.agent.md`

**What to build (longer term):**
- Agent role types: coordinator, lead, scout, builder, reviewer
- Hierarchical dispatch: coordinator → lead → workers
- Non-overlapping file scope enforcement
- Merge-ready signaling protocol
- This is a larger architectural change — start with the lifecycle/checkpoint foundation first

## Reference Project Summary

| Project | Language | Key Concepts to Borrow |
|---|---|---|
| overstory | TypeScript | Agent identity, lifecycle/checkpoint/handoff, hierarchical roles (coordinator/lead/scout/builder/reviewer), mail-based communication, expertise recording |
| spacebot | Rust | Worker state machine, context compaction (background/aggressive/emergency), cortex bulletin system, status blocks, segment-based execution |
| persistent-swarm | Markdown/JS | Stateful orchestrator with filesystem persistence, phase-based lifecycle, approval gates, revision/delta workflows |
| kimi-cli | Python | Agent flow (flowchart-driven execution), skill system, soul architecture |
| CLIO | Perl | Multi-agent coordination docs, remote execution |
| mulch | TypeScript | Expertise/knowledge management, seed-based issue tracking |
| pixel-agents | TypeScript | Visual agent representation (relevant for our Godot viewport) |

## Suggested Implementation Order

1. **Agent checkpoint/identity** — foundation for everything else
2. **Worker state machine** — formal states + transitions in AgentActor
3. **Status block** — aggregated live status
4. **Context compaction** — token monitoring + compaction triggers
5. **Cortex bulletin** — periodic memory consolidation
6. **Hierarchical roles** — longer-term architectural evolution

## Files to Modify

Primary:
- `project/plugins/Plugin.Actors/AgentActor.cs` — state machine, checkpoint, compaction
- `project/plugins/Plugin.Actors/AgentWorldSystem.cs` — new actors (cortex, status)
- `project/contracts/Contracts.Core/Messages.cs` — new message types

New files:
- `project/plugins/Plugin.Actors/AgentCheckpointActor.cs`
- `project/plugins/Plugin.Actors/AgentStatusActor.cs`
- `project/plugins/Plugin.Actors/CortexActor.cs`
- `project/contracts/Contracts.Core/AgentIdentity.cs`
- `project/contracts/Contracts.Core/AgentCheckpoint.cs`
