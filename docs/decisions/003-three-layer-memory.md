# ADR-003: Three-Layer Memory Architecture

Date: 2026-03-01
Status: Proposed
Depends On: ADR-001

## Context

Giant-isopod currently has a single memory abstraction: per-agent Memvid files (`.mv2`)
managed by `MemorySupervisorActor` → `MemvidActor`. This provides semantic search over
an agent's personal history, but it conflates three distinct concerns:

1. **Working memory** — what is the agent doing *right now*? What tasks are in flight?
   What partial results exist? This is ephemeral and fast.
2. **Shared memory** — what do other agents need to know? Intermediate artifacts, signals
   like "I finished indexing module X", coordination state. This is cross-agent and
   medium-lived.
3. **Long-term memory** — what happened in past sessions? Outcome history, learned
   patterns, semantic knowledge. This is persistent and slow (embedding search).

Swimming-tuna separates these into four layers (TaskRegistry, BlackboardActor, ArcadeDB,
Langfuse). CLIO has session memory + long-term storage. Kimi has context + compaction.
The pattern is consistent: one memory layer doesn't fit all access patterns.

## Decision

Split memory into three layers with distinct actors, access patterns, and lifetimes.

## Design

### Layer 1: Working Memory (per-agent, ephemeral)

**What**: Current task context, in-flight state, partial results, tool outputs.

**Lifetime**: Lives as long as the agent. Cleared between tasks or on agent restart.

**Implementation**: Already partially exists in `AgentTaskActor` (tracks active tasks).
Formalize as a `Dictionary<string, object>` scratch space inside `AgentActor`, accessible
via messages.

```csharp
// ── Working memory ──
public record SetWorkingMemory(string AgentId, string Key, string Value);
public record GetWorkingMemory(string AgentId, string Key);
public record WorkingMemoryValue(string AgentId, string Key, string? Value);
public record ClearWorkingMemory(string AgentId);
```

**Access pattern**: Key-value get/set. Sub-millisecond. Agent-local only.

**Actor path**: Part of `AgentActor` state (no separate actor needed).

### Layer 2: Shared Memory — Blackboard (cross-agent, session-lived)

**What**: Signals and artifacts that agents publish for other agents to observe. Examples:
"codebase indexed", "module A built successfully", "test results for commit abc123".

**Lifetime**: Lives for the swarm session. Survives individual agent restarts but not
system restart (unless persisted).

**Implementation**: New `BlackboardActor` at `/user/blackboard`. Uses Akka EventStream
for pub/sub — agents subscribe to keys they care about.

```csharp
// ── Blackboard (shared memory) ──
public record PublishSignal(string Key, string Value, string? PublisherId = null);
public record QuerySignal(string Key);
public record SignalValue(string Key, string? Value, string? PublisherId = null);
public record SubscribeSignal(string Key);  // subscriber gets SignalValue on updates
```

**Design notes**:

- Inspired by swimming-tuna's `BlackboardActor` (stigmergy pattern).
- `PublishSignal` stores locally and publishes to EventStream.
- Subscribing actors receive `SignalValue` whenever the key changes.
- Task-scoped keys (e.g., `task:{taskId}:result`) enable per-task coordination.
- Global keys (e.g., `index:status`) enable swarm-wide signals.
- No embedding search — pure key-value with optional prefix listing.

```
ActorSystem "agent-world"
├── /user/registry
├── /user/memory            ← Layer 3 (long-term, Memvid)
├── /user/blackboard         ← NEW: Layer 2 (shared, session-scoped)
├── /user/agents
├── /user/dispatch
├── /user/taskgraph
└── /user/viewport
```

### Layer 3: Long-Term Memory (per-agent, persistent)

**What**: Semantic knowledge, outcome history, learned patterns. Survives across sessions.

**Lifetime**: Permanent (until explicitly pruned).

**Implementation**: Existing `MemorySupervisorActor` → `MemvidActor` with `.mv2` files.
No changes needed — this layer is already designed correctly.

```csharp
// Existing messages (unchanged)
public record StoreMemory(string AgentId, string Content, string? Title = null, ...);
public record SearchMemory(string AgentId, string Query, int TopK = 10);
public record MemorySearchResult(string AgentId, IReadOnlyList<MemoryHit> Hits);
```

**Access pattern**: Embedding-based semantic search. Tens of milliseconds. Per-agent.

**TODO**: Complete the `MemvidActor` CliWrap integration (currently stubbed).

### Summary Table

| Layer | Actor | Scope | Lifetime | Access | Latency |
|-------|-------|-------|----------|--------|---------|
| Working | `AgentActor` (internal) | Per-agent | Per-task | Key-value | <1ms |
| Shared | `/user/blackboard` | Cross-agent | Session | Key-value + pub/sub | ~1ms |
| Long-term | `/user/memory/{agentId}` | Per-agent | Persistent | Semantic search | ~50ms |

### Data Flow Example

```
Agent A finishes building module X:
  1. Stores build artifact path in Working Memory (self-reference)
  2. Publishes "module:X:built" = "success" to Blackboard (other agents see it)
  3. Stores "Built module X with approach Y, took 45s" in Long-Term Memory (future learning)

Agent B needs module X:
  1. Queries Blackboard for "module:X:built" → gets signal
  2. Proceeds with its task (dependency satisfied)
  3. Never touches Agent A's Working or Long-Term memory
```

## Trade-offs

### Why three layers, not one

A single Memvid store is too slow for working memory (~50ms per lookup vs <1ms key-value)
and too agent-scoped for shared coordination. Agents currently have no way to signal each
other without going through `DispatchActor`, which is a task-assignment channel, not a
data-sharing channel.

### Why not four layers (add learning/telemetry)

Swimming-tuna has a fourth layer (Langfuse for outcome similarity search). This is valuable
but premature for giant-isopod — we don't yet have enough outcome data to learn from. When
we do, it can be added as a query interface over Layer 3 (Memvid already supports semantic
search, which covers similarity).

### Risk

- Blackboard keys are untyped strings. Schema drift is possible. Mitigate with naming
  conventions (e.g., `task:{id}:*`, `agent:{id}:*`, `index:*`).
- EventStream pub/sub is in-process only. If we distribute actors across nodes later,
  Blackboard needs to move to a distributed pub/sub (Akka.Cluster.PubSub or similar).

## References

- ADR-001: Skill-Based Tooling Over Actor-Per-Tool
- Swimming-tuna `BlackboardActor` (stigmergy via EventStream)
- Swimming-tuna `TaskRegistry` (in-memory working state)
- CLIO `Memory/` module (short-term + long-term separation)
- Kimi `context.py` + `compaction.py` (context management + pruning)
