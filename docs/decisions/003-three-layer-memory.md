# ADR-003: Four-Layer Memory Architecture

Date: 2026-03-01 (revised 2026-03-01)
Status: Proposed (revised)
Depends On: ADR-001

## Context

Giant-isopod currently has a single memory abstraction: per-agent Memvid files (`.mv2`)
managed by `MemorySupervisorActor` → `MemvidActor`. This provides semantic search over
an agent's personal history, but it conflates four distinct concerns:

1. **Working memory** — what is the agent doing *right now*? What tasks are in flight?
   What partial results exist? This is ephemeral and fast.
2. **Shared memory** — what do other agents need to know? Intermediate artifacts, signals
   like "I finished indexing module X", coordination state. This is cross-agent and
   medium-lived.
3. **Episodic memory** — what has the agent done *during this task*? Process notes,
   intermediate findings, decisions made, errors encountered. Like a person's notebook
   while working on a specific job. Scoped to a task run.
4. **Long-term memory** — what has the agent learned across many tasks and sessions?
   Accumulated experience, codebase familiarity, proven approaches. Persistent and
   cross-session.

Swimming-tuna separates these into four layers (TaskRegistry, BlackboardActor, ArcadeDB,
Langfuse). CLIO has session memory + long-term storage. Kimi has context + compaction.
The pattern is consistent: one memory layer doesn't fit all access patterns.

The original ADR-003 used Memvid as the long-term memory layer, but this conflates
episodic and long-term concerns. An agent recording "I tried approach X and it failed
because of Y" during a task (episodic) is fundamentally different from "approach X
generally fails in this codebase" (long-term learned knowledge). Memvid's append-and-search
model fits episodic memory well; long-term persistent knowledge needs an embedded database.

## Decision

Split memory into four layers with distinct actors, access patterns, and lifetimes.

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
├── /user/memory            ← Layer 3 (episodic, Memvid per task)
├── /user/knowledge          ← Layer 4 (long-term, embedded DB — future)
├── /user/blackboard         ← Layer 2 (shared, session-scoped)
├── /user/agents
├── /user/dispatch
├── /user/taskgraph
└── /user/viewport
```

### Layer 3: Episodic Memory (per-task, Memvid)

**What**: The agent's process journal for the current task run. Records what the agent
did, what it found, what worked and what failed. Like a person's handwritten notes while
working on a specific assignment.

**Lifetime**: Scoped to a task run. Created when a task is assigned, persists until the
task completes (or is explicitly discarded). Can optionally be promoted to long-term
memory on task success.

**Implementation**: Existing `MemorySupervisorActor` → `MemvidActor` with `.mv2` files,
but **scoped per task** instead of per agent lifetime. When a task starts, `MemvidActor`
opens/creates a task-scoped `.mv2` file. When the task ends, the file can be archived
or its key findings promoted to Layer 4.

```csharp
// Existing messages (scoping clarified)
public record StoreMemory(string AgentId, string Content, string? Title = null, ...);
public record SearchMemory(string AgentId, string Query, int TopK = 10);
public record MemorySearchResult(string AgentId, IReadOnlyList<MemoryHit> Hits);
```

**Access pattern**: Append-heavy writing, semantic search for recall. Per-agent, per-task.

**Examples of episodic entries**:
- "Tried refactoring with Strategy pattern — compile error in module X due to circular dep"
- "Found the bug: null check missing in `ProcessEvent` handler at line 47"
- "API rate limit hit after 50 calls, switched to batch endpoint"

**TODO**: Complete the `MemvidActor` CliWrap integration (currently stubbed). Add
task-scoped file management.

### Layer 4: Long-Term Memory (per-agent, persistent, embedded DB)

**What**: Accumulated knowledge across many tasks and sessions. Proven patterns, codebase
familiarity, successful approaches, known pitfalls. Like a person's professional experience.

**Lifetime**: Permanent (until explicitly pruned or decayed).

**Implementation**: New `KnowledgeStoreActor` at `/user/knowledge/{agentId}`, backed by
an embedded database (SQLite with FTS5, or LiteDB). Not yet implemented.

```csharp
// ── Long-term knowledge ──
public record StoreKnowledge(string AgentId, string Content, string Category,
    IDictionary<string, string>? Tags = null);
public record QueryKnowledge(string AgentId, string Query, string? Category = null, int TopK = 10);
public record KnowledgeResult(string AgentId, IReadOnlyList<KnowledgeEntry> Entries);
public record KnowledgeEntry(string Content, string Category, double Relevance,
    IDictionary<string, string> Tags, DateTimeOffset StoredAt);
```

**Access pattern**: Full-text search + tag filtering. Per-agent. Cross-session persistent.

**Categories**: `pattern`, `pitfall`, `codebase`, `preference`, `outcome`.

**Examples of long-term entries**:
- Category `pitfall`: "ModernSatsuma graphs must use Directedness.Directed for DAG operations"
- Category `pattern`: "In this codebase, actors use IWithTimers for deadline enforcement"
- Category `outcome`: "Batch processing module X: Strategy pattern works, 3min build time"

**Promotion from episodic**: When a task succeeds, key findings from episodic memory can
be distilled and stored as long-term knowledge. This can be automated (summarize episodic
entries on task completion) or explicit (agent decides what's worth remembering).

**TODO**: Design embedded DB schema. Implement `KnowledgeStoreActor`. Define promotion
pipeline from episodic → long-term.

### Summary Table

| Layer | Actor | Scope | Lifetime | Access | Latency |
|-------|-------|-------|----------|--------|---------|
| Working | `AgentActor` (internal) | Per-agent | Per-task | Key-value | <1ms |
| Shared | `/user/blackboard` | Cross-agent | Session | Key-value + pub/sub | ~1ms |
| Episodic | `/user/memory/{agentId}` | Per-agent, per-task | Task run | Semantic search (Memvid) | ~50ms |
| Long-term | `/user/knowledge/{agentId}` | Per-agent | Persistent | FTS + tag filter (embedded DB) | ~10ms |

### Data Flow Example

```
Agent A working on "build module X":
  1. Stores build artifact path in Working Memory (ephemeral self-reference)
  2. Records "tried approach Y, got error Z" in Episodic Memory (task journal)
  3. Publishes "module:X:built" = "success" to Blackboard (other agents see it)
  4. On task completion: key finding promoted to Long-Term Memory:
     "Module X: approach Y works, takes 45s. Avoid approach Z (circular dep)."

Agent B starts a new task that touches module X:
  1. Queries Blackboard for "module:X:built" → gets signal (coordination)
  2. Queries Long-Term Memory for "module X" → gets Agent A's distilled knowledge
  3. Uses findings to avoid Agent A's mistakes, picks better approach
  4. Records own episodic notes as it works
```

## Trade-offs

### Why four layers, not one

A single Memvid store is too slow for working memory (~50ms per lookup vs <1ms key-value)
and too agent-scoped for shared coordination. Agents currently have no way to signal each
other without going through `DispatchActor`, which is a task-assignment channel, not a
data-sharing channel.

### Why separate episodic from long-term

Memvid's append-and-search model fits episodic memory perfectly: an agent records what
it's doing as it works, and can search back over recent entries. But long-term knowledge
needs different properties:

- **Structured categories and tags** — not just free-text semantic search
- **Cross-session persistence** — Memvid files are per-agent, but long-term knowledge
  should survive agent restarts and be queryable by fresh agent instances
- **Curation** — episodic memory is raw notes, long-term memory is distilled knowledge.
  Not everything in the task journal deserves permanent storage.
- **Decay/pruning** — long-term entries should have relevance scoring and staleness
  detection, which requires structured metadata (timestamps, hit counts, categories)

An embedded DB (SQLite with FTS5) provides all of these naturally.

### Why not five layers (add telemetry)

Swimming-tuna has Langfuse for outcome similarity search. This overlaps with long-term
memory's `outcome` category. If telemetry needs diverge significantly (e.g., real-time
dashboards, A/B testing), a fifth layer can be added later.

### Risk

- Blackboard keys are untyped strings. Schema drift is possible. Mitigate with naming
  conventions (e.g., `task:{id}:*`, `agent:{id}:*`, `index:*`).
- EventStream pub/sub is in-process only. If we distribute actors across nodes later,
  Blackboard needs to move to a distributed pub/sub (Akka.Cluster.PubSub or similar).
- Episodic → long-term promotion requires summarization, which may need LLM calls.
  Keep promotion optional and manual until the pipeline is proven.

## References

- ADR-001: Skill-Based Tooling Over Actor-Per-Tool
- Swimming-tuna `BlackboardActor` (stigmergy via EventStream)
- Swimming-tuna `TaskRegistry` (in-memory working state)
- CLIO `Memory/` module (short-term + long-term separation)
- Kimi `context.py` + `compaction.py` (context management + pruning)
