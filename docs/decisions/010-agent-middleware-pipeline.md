# ADR-010: Agent Middleware Pipeline

Date: 2026-03-03
Status: Proposed
Depends On: ADR-002, ADR-004, ADR-008, ADR-009

## Context

Giant-isopod now has real worktree execution, artifact validation, long-term knowledge,
episodic memory, and runtime-aware dispatch. The cost is that cross-cutting concerns are
spread across multiple actors:

1. **Prompt enrichment is hardcoded** in `AgentActor` and `PromptBuilder`.
2. **Runtime observability and retries are hardcoded** in `AgentRuntimeActor`.
3. **Validation policy is split** between `TaskGraphActor`, `ValidatorActor`, and runtime prompts.
4. **Runtime-specific behavior leaks upward** into actor logic instead of being composed at the edge.

The result is feature coupling: adding approval checks, telemetry, budget tracking, or
scope guards requires editing core actor flow instead of inserting behavior at a stable seam.

## Decision

Add a **home-grown middleware pipeline** around agent task execution, runtime invocation,
and validator review. Do not replace the actor model. Actors remain the state machines and
message coordinators; middleware becomes the pluggable behavior layer around each stage.

## Design

### Three Middleware Chains

Giant-isopod should have three distinct middleware chains:

1. **Task middleware** — wraps task preparation and post-task learning.
2. **Runtime middleware** — wraps CLI/API runtime launch, streaming, retries, and telemetry.
3. **Review middleware** — wraps artifact review preparation and result normalization.

These are separate because they have different contexts and failure policies.

### Core Interfaces

```csharp
public delegate Task AgentTaskMiddlewareDelegate(AgentTaskContext context, CancellationToken cancellationToken);

public interface IAgentTaskMiddleware
{
    Task InvokeAsync(
        AgentTaskContext context,
        AgentTaskMiddlewareDelegate next,
        CancellationToken cancellationToken);
}

public delegate Task RuntimeMiddlewareDelegate(RuntimeExecutionContext context, CancellationToken cancellationToken);

public interface IRuntimeMiddleware
{
    Task InvokeAsync(
        RuntimeExecutionContext context,
        RuntimeMiddlewareDelegate next,
        CancellationToken cancellationToken);
}

public delegate Task ReviewMiddlewareDelegate(ReviewContext context, CancellationToken cancellationToken);

public interface IReviewMiddleware
{
    Task InvokeAsync(
        ReviewContext context,
        ReviewMiddlewareDelegate next,
        CancellationToken cancellationToken);
}
```

### Context Objects

#### AgentTaskContext

Carries:

- `AgentId`
- `TaskAssigned`
- `PreferredRuntimeId`
- `OwnedPaths`
- `ExpectedFiles`
- retrieved knowledge entries
- prior subtask outputs
- prompt fragments / final prompt
- mutable execution annotations (telemetry tags, warnings, policy decisions)

This context is produced by `AgentActor` before runtime launch.

#### RuntimeExecutionContext

Carries:

- runtime id / runtime config
- working directory
- resolved arguments and environment
- prompt / prompt file metadata
- stdout/stderr transcript
- structured result
- retry count
- launch diagnostics

This context is produced by `AgentRuntimeActor` just before runtime execution.

#### ReviewContext

Carries:

- validator spec
- artifact metadata
- task description
- owner path constraints
- review prompt
- parsed review result
- validator annotations

This context is produced by `ValidatorActor` before review runtime execution.

### Initial Middleware Set

#### Task Middleware

1. **KnowledgeEnrichmentMiddleware**
   - query long-term knowledge and episodic memory
   - inject retrieved entries into prompt inputs

2. **PathScopeMiddleware**
   - assert `OwnedPaths` / `ExpectedFiles` are present when required
   - normalize path patterns

3. **TaskBudgetMiddleware**
   - attach budget/timing metadata
   - fail fast if the task has no remaining budget

4. **TaskLearningMiddleware**
   - after completion: persist outcome, pitfall, and planning knowledge

#### Runtime Middleware

1. **LaunchDiagnosticsMiddleware**
   - record executable, resolved args, working directory, prompt hash

2. **PromptTransportMiddleware**
   - choose direct arg vs prompt file vs stdin transport
   - runtime-specific prompt shaping belongs here, not in actor flow

3. **StructuredResultMiddleware**
   - parse `<giant-isopod-result>`
   - normalize no-op, failure, and subplan outputs

4. **RuntimeRetryMiddleware**
   - apply bounded retries for invalid envelope, off-task completion, or transient runtime failures

5. **RuntimeTelemetryMiddleware**
   - emit runtime start/exit/output events consistently

#### Review Middleware

1. **ArtifactScopeReviewMiddleware**
   - compare artifact paths against `OwnedPaths` / `ExpectedFiles`
   - fail before model review when scope is violated

2. **ReviewPromptMiddleware**
   - build task-specific review rubric
   - include artifact excerpts and validator config

3. **StructuredReviewMiddleware**
   - parse `<giant-isopod-review>`
   - normalize issues and pass/fail result

### Actor Integration

#### AgentActor

`AgentActor` should stop manually doing:

- knowledge retrieval
- prompt enrichment
- post-task outcome/pitfall persistence

Instead:

1. create `AgentTaskContext`
2. run task middleware chain
3. hand the final prompt/context to `AgentRuntimeActor`

#### AgentRuntimeActor

`AgentRuntimeActor` should stop manually owning:

- transport decisions
- structured output parsing
- most retry rules
- launch telemetry construction

Instead:

1. create `RuntimeExecutionContext`
2. run runtime middleware chain
3. emit `TaskCompleted` / `TaskFailed` from normalized context

#### ValidatorActor

`ValidatorActor` should stop manually owning:

- prompt assembly
- scope checks mixed into review logic
- review result parsing

Instead:

1. create `ReviewContext`
2. run review middleware chain
3. emit `ValidatorResult`

### Ordering Rules

Middleware ordering must be explicit and deterministic. The initial default order should be:

#### Task Chain

1. `PathScopeMiddleware`
2. `KnowledgeEnrichmentMiddleware`
3. `TaskBudgetMiddleware`
4. core task-prompt preparation
5. `TaskLearningMiddleware` (post-execution stage)

#### Runtime Chain

1. `LaunchDiagnosticsMiddleware`
2. `PromptTransportMiddleware`
3. core runtime execution
4. `StructuredResultMiddleware`
5. `RuntimeRetryMiddleware`
6. `RuntimeTelemetryMiddleware`

#### Review Chain

1. `ArtifactScopeReviewMiddleware`
2. `ReviewPromptMiddleware`
3. core review runtime execution
4. `StructuredReviewMiddleware`

### Why Home-Made Instead of Framework Middleware

- The current system is actor-first and message-driven; generic ASP.NET-style middleware is not the right abstraction.
- Runtime and review stages have domain-specific contexts that are stronger than plain request/response.
- This keeps Giant-isopod's orchestration logic local and testable without bringing in a new framework dependency.

## Trade-offs

### Benefits

- New cross-cutting behavior becomes additive instead of invasive.
- Runtime-specific prompt transport can vary per CLI without infecting core actor logic.
- Review, telemetry, approval, and learning features gain clean extension points.
- Middleware order becomes auditable and testable.

### Costs

- More moving parts and more context classes.
- Debugging requires understanding both actor flow and middleware order.
- Bad middleware ordering can cause subtle behavior drift if not tested.

## Implementation Plan

### Phase 1

1. Introduce middleware interfaces and context records.
2. Add a task middleware pipeline to `AgentActor`.
3. Move knowledge retrieval and post-task learning behind middleware.

### Phase 2

1. Add a runtime middleware pipeline to `AgentRuntimeActor`.
2. Move launch diagnostics, prompt transport, structured parsing, and retries behind middleware.

### Phase 3

1. Add a review middleware pipeline to `ValidatorActor`.
2. Move path-scope prechecks and review prompt assembly behind middleware.

### Phase 4

1. Add middleware registration/configuration to `AgentWorldSystem`.
2. Add tests proving middleware ordering and short-circuit behavior.

## References

- Discussion: `docs/_inbox/2026-03-03-203329-local-command-caveatcaveat-the-messages-below-w-002.txt`
- ADR-002: Task Graph (DAG) via ModernSatsuma
- ADR-004: Market-First Coordination
- ADR-008: Artifact Registry
- ADR-009: Progressive Task Decomposition
