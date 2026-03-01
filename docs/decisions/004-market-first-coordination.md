# ADR-004: Market-First Coordination with Orchestrator Fallback

Date: 2026-03-01
Status: Proposed
Depends On: ADR-001, ADR-002

## Context

Giant-isopod's `DispatchActor` uses orchestrator-first coordination: it queries the
`SkillRegistryActor` for capable agents and picks the first match (`result.AgentIds[0]`).
This is simple but has problems:

1. **No load awareness** — a busy agent gets tasks dumped on it while idle agents wait.
2. **No fitness signal** — all capable agents are treated equally regardless of context,
   current workload, or specialization depth.
3. **No autonomy** — agents are passive recipients. In a swarm, agents should self-select
   based on their own state.

Swimming-tuna also uses orchestrator-first (CoordinatorActor drives the pipeline). Overstory
and Persistent Swarm are fully orchestrator-driven. CLIO uses a broker (centralized).

Market-first is different: agents **bid** on tasks they want, competing on fitness and
availability. The dispatcher becomes an **auctioneer** rather than an **assigner**.

## Decision

Replace first-match assignment in `DispatchActor` with a **bid-collect-select** cycle.
Keep orchestrator semantics as a timeout fallback (hybrid approach).

## Design

### Bid Protocol

```csharp
// ── Market coordination ──
public record TaskAvailable(string TaskId, string Description, IReadOnlySet<string> RequiredCapabilities, TimeSpan BidWindow);
public record TaskBid(string TaskId, string AgentId, double Fitness, int ActiveTaskCount, TimeSpan EstimatedDuration);
public record TaskAwardedTo(string TaskId, string AgentId);
public record TaskBidRejected(string TaskId, string AgentId);  // agent lost the auction
```

### Dispatch Flow (Market Phase)

```
1. DispatchActor receives TaskRequest (or TaskReadyForDispatch from TaskGraphActor)
2. Queries SkillRegistryActor for capable agents (unchanged)
3. Broadcasts TaskAvailable to all capable agents with BidWindow (default 500ms)
4. Agents evaluate:
   - Do I have capacity? (ActiveTaskCount < MaxConcurrent)
   - How well do my skills match? (Fitness score 0.0–1.0)
   - How long will this take me? (EstimatedDuration)
5. Agents that want the task reply with TaskBid
6. After BidWindow expires, DispatchActor selects winner:
   - Primary: highest Fitness
   - Tiebreak: lowest ActiveTaskCount
   - Tiebreak: shortest EstimatedDuration
7. Sends TaskAwardedTo to winner, TaskBidRejected to losers
8. Winner receives TaskAssigned (existing flow continues)
```

### Fallback (Orchestrator Phase)

If no bids arrive within the BidWindow:

```
9. Fall back to first-match assignment (current behavior)
10. Log warning: "No bids received for task {TaskId}, using first-match fallback"
```

This ensures the system never stalls waiting for bids.

### Agent-Side Bidding

Bidding logic lives inside `AgentActor`. Each agent evaluates `TaskAvailable` locally:

```csharp
// Inside AgentActor.OnReceive
case TaskAvailable available:
    if (_activeTasks.Count >= _maxConcurrent) break;  // at capacity, don't bid

    var fitness = ComputeFitness(available.RequiredCapabilities, _myCapabilities);
    if (fitness < _minBidThreshold) break;  // not a good fit, don't bid

    Sender.Tell(new TaskBid(
        available.TaskId,
        _agentId,
        fitness,
        _activeTasks.Count,
        EstimateDuration(available.Description)));
    break;
```

**Fitness computation** (initial, simple):

```
fitness = |requiredCapabilities ∩ myCapabilities| / |requiredCapabilities|
```

A perfect match is 1.0. An agent with extra capabilities beyond what's required still
scores 1.0 (superset). An agent missing capabilities scores < 1.0 and is filtered out
by the registry query anyway.

Future refinements:

- Weight capabilities by specialization depth (e.g., agent that has used `code_edit`
  1000 times vs 10 times).
- Factor in recent success rate per capability.
- Factor in memory relevance (does this agent have context for this task's domain?).

### DispatchActor Changes

`DispatchActor` gains a `BidCollector` behavior (Akka `BecomeStacked`):

```
Normal → receives TaskRequest
  → queries registry
  → broadcasts TaskAvailable
  → BecomeStacked(BidCollector)

BidCollector:
  → collects TaskBid messages
  → on BidWindow timeout (ScheduleOnce):
     → if bids.Count > 0: select winner, award
     → if bids.Count == 0: fallback to first-match
  → UnbecomeStacked
```

### Concurrency

Multiple tasks can be in the bid-collection phase simultaneously. Each `TaskRequest`
creates an independent bid session keyed by `TaskId`. `BidCollector` handles interleaved
bids from different tasks.

### Actor Tree (Unchanged)

No new actors. `DispatchActor` gains the bid protocol. `AgentActor` gains bid evaluation.
The market is an interaction pattern, not a separate component.

## Trade-offs

### Why market-first fits giant-isopod

- **Swarm philosophy**: Agents are autonomous entities with skills, not passive workers.
  Bidding makes agents self-selecting, which matches the ECS visualization of agents
  "choosing" tasks.
- **Natural load balancing**: Busy agents don't bid. No central load tracker needed.
- **Extensible**: Fitness can incorporate memory, recent outcomes, specialization depth —
  the market gets smarter without changing the protocol.

### Why not pure market (no fallback)

- Cold start: when agents are just spawning, they may not respond in time.
- Determinism: for testing and debugging, first-match fallback ensures tasks always
  get assigned.
- Simplicity: the fallback is literally the current code — zero risk.

### Why not pure orchestrator (swimming-tuna style)

- Swimming-tuna's CoordinatorActor works because it has a fixed role pipeline
  (planner → builder → reviewer). Giant-isopod's agents are heterogeneous with
  dynamic skill bundles — a central orchestrator can't know each agent's current
  fitness as well as the agent itself.

### Risk

- **Bid storms**: If 50 agents all bid on every task, message volume grows. Mitigate by
  only broadcasting to capable agents (registry pre-filter) and keeping BidWindow short.
- **Gaming**: An agent could always bid fitness=1.0. Not a real concern since we control
  the agent implementations, but worth noting for future multi-tenant scenarios.
- **Latency**: BidWindow adds 500ms to dispatch. Acceptable for swarm workloads (tasks
  take minutes, not milliseconds). Configurable down to 100ms if needed.

## References

- ADR-001: Skill-Based Tooling Over Actor-Per-Tool
- ADR-002: Task Graph (DAG) via ModernSatsuma
- Current `DispatchActor`: `Plugin.Actors/DispatchActor.cs` (first-match assignment)
- Current `SkillRegistryActor`: `Plugin.Actors/SkillRegistryActor.cs` (capability index)
- Swimming-tuna `CoordinatorActor` (orchestrator-first reference)
- Overstory agent spawning with capability-based `ov sling` (orchestrator reference)
