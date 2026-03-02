# ADR-011: Validator Framework

Date: 2026-03-02
Status: Proposed
Depends On: ADR-008

## Context

Giant-isopod currently has no formal mechanism for verifying task outputs. `TaskCompleted`
is accepted at face value — there is no automated check that code compiles, tests pass,
images meet resolution requirements, or documents follow templates.

This matters because:

1. **Quality gating** — Without validators, bad outputs propagate to downstream tasks,
   causing cascading failures that are expensive to debug.
2. **Uniform "done" definition** — "Done" should mean "validators green" regardless of
   artifact type. Today "done" means "the agent said so."
3. **Multi-modal outputs** — Code, images, audio, 3D models, and docs each have different
   quality criteria. A pluggable validator system makes these checkable without hardcoding
   per-type logic in the orchestrator.
4. **Revision loop** — When validation fails, the system should automatically request
   revision (same agent, same task) or spawn a fix-it subtask (ADR-009), rather than
   requiring human intervention.

## Decision

Introduce a `ValidatorActor` that runs pluggable validators against artifacts after task
completion. Validators are registered per artifact type and can be machine-checkable (scripts,
CLI tools) or agent-checkable (a critic agent evaluates with a rubric). Task completion is
gated on validator results.

## Design

### Validator Model

```csharp
public record ValidatorSpec(
    string Name,                   // "compile", "unit-tests", "resolution-check"
    ValidatorKind Kind,
    ArtifactType AppliesTo,        // which artifact type this validates
    string Command,                // for Script kind: CLI command to run
    string? Rubric,                // for AgentReview kind: evaluation criteria
    Dictionary<string, string>? Config  // validator-specific settings
);

public enum ValidatorKind
{
    Script,       // machine-checkable: run a command, check exit code
    AgentReview   // agent-checkable: a critic agent evaluates against a rubric
}
```

### Default Validators

| Artifact Type | Validator | Kind | Check |
|---------------|-----------|------|-------|
| Code | `compile` | Script | `dotnet build` exit code 0 |
| Code | `unit-tests` | Script | `dotnet test` exit code 0 |
| Code | `lint` | Script | configured linter passes |
| Code | `clean-status` | Script | `git status --porcelain` is empty |
| Doc | `template-check` | Script | required sections present (regex) |
| Doc | `spell-check` | Script | aspell/cspell pass |
| Image | `resolution` | Script | `identify` confirms min dimensions |
| Image | `format-check` | Script | file format matches expected |
| Audio | `probe` | Script | `ffprobe` confirms sample rate, duration, peak |
| Model3D | `gltf-validate` | Script | `gltf_validator` passes |
| Model3D | `polycount` | Script | polycount below threshold |
| App | `smoke-test` | Script | build artifact runs, exits cleanly |

Additional validators can be registered dynamically. The defaults above cover the
common cases; teams add domain-specific validators as needed.

### Messages

```csharp
// ── Validation requests ──
public record ValidateArtifact(string ArtifactId, ArtifactRef Artifact);
public record ValidationComplete(string ArtifactId, IReadOnlyList<ValidatorResult> Results);

// ── Validator registration ──
public record RegisterValidator(ValidatorSpec Spec);
public record ValidatorRegistered(string Name);

// ── Revision requests ──
public record RevisionRequested(
    string TaskId,
    string ArtifactId,
    IReadOnlyList<ValidatorResult> Failures,
    int AttemptNumber
);
```

### ValidatorActor

```
/user/validator — ValidatorActor
    State:
      Dictionary<ArtifactType, List<ValidatorSpec>> validators (by type)
      int maxRevisionAttempts (default: 2)

    OnReceive:
      RegisterValidator → add to validators map by AppliesTo type

      ValidateArtifact →
        1. Look up validators for artifact.Type
        2. For each Script validator:
           - Run Command with artifact.Uri as input
           - Capture exit code + stdout/stderr → ValidatorResult
        3. For each AgentReview validator:
           - Send review request to a critic agent via DispatchActor
           - Critic returns pass/fail + details
        4. Aggregate results → send ValidationComplete
        5. Send UpdateValidation(artifactId, result) to ArtifactRegistryActor for each result

      ValidationComplete (self, after aggregation) →
        If all passed → artifact is valid, flow continues
        If any failed →
          If attemptNumber < maxRevisionAttempts:
            Send RevisionRequested to DispatchActor (re-auction to same or new agent)
          Else:
            Escalate: mark task as failed, notify orchestrator
```

### Integration with Task Flow

After `TaskCompleted` is processed and artifacts are registered (ADR-008):

```
1. TaskGraphActor sends ValidateArtifact for each artifact to ValidatorActor
2. ValidatorActor runs validators, returns ValidationComplete
3. If all pass:
   - TaskGraphActor marks task as truly complete
   - Downstream tasks become ready
   - If code artifact: RequestMerge to WorkspaceActor (ADR-010)
4. If any fail:
   - ValidatorActor sends RevisionRequested
   - DispatchActor re-auctions the task with failure context
   - Agent receives the task again with validator feedback
   - Attempt counter increments
5. After max attempts: task fails, parent handles via decomposition (ADR-009)
```

### Task Acceptance Criteria

Tasks can specify required validators in their definition:

```csharp
public record TaskDefinition(
    string TaskId,
    string Description,
    IReadOnlySet<string> RequiredCapabilities,
    IReadOnlyList<string>? RequiredValidators,  // NEW: e.g. ["compile", "unit-tests"]
    IReadOnlyDictionary<string, string>? ValidatorConfig  // overrides, e.g. {"resolution": "2048"}
);
```

If `RequiredValidators` is null, the default validators for the artifact type apply.
If specified, only the listed validators run (allows skipping expensive checks for
draft/exploration tasks).

### Agent-Review Validators (Critic Pattern)

For subjective quality (art style, writing tone, UX flow), `AgentReview` validators
dispatch a review task to a critic agent:

```
1. ValidatorActor creates a review task:
   TaskRequest(description: "Review artifact {id} against rubric: {rubric}")
2. DispatchActor auctions it (agents with "review" capability bid)
3. Critic agent evaluates, returns pass/fail + detailed feedback
4. ValidatorActor incorporates result
```

This reuses the existing auction flow. Critic agents are just agents with a "review"
capability — no special infrastructure needed.

## Trade-offs

### Why actor-based (not a CI pipeline)

- Validators need to integrate with the Akka message flow (revision requests, artifact
  registry updates). An external CI system would require polling and webhook bridges.
- For small-scale swarm execution, in-process validation is faster (no container spin-up).
- CI integration can be added later as a Script validator that calls `gh workflow run`.

### Why revision loop (not immediate failure)

- Many validator failures are fixable: compilation errors, lint issues, missing sections.
  Giving the agent one or two retries with feedback is cheaper than failing the whole task
  and re-decomposing.
- Max attempt cap (default 2) prevents infinite retry loops.

### Why pluggable (not hardcoded per type)

- Different projects have different quality bars. A prototype might skip unit tests.
  A production pipeline might require security scans.
- `RegisterValidator` allows runtime customization without code changes.

### Risk

- **Validator execution time** — Some validators (full test suite, 3D render check) are
  slow. Mitigate with timeouts per validator and allowing tasks to specify which validators
  to run.
- **False positives** — Agent-review validators are subjective. A critic agent might reject
  valid work. Mitigate with rubric clarity and allowing the original agent to contest
  (escalate to human).
- **Script security** — Script validators run shell commands. Mitigate by sandboxing
  validator execution and only allowing registered commands (no user-supplied scripts
  without approval).

## References

- ADR-008: Artifact Registry (ValidatorResult stored with artifacts)
- ADR-009: Progressive Task Decomposition (fix-it subtask on persistent failure)
- ADR-010: Workspace Lifecycle (code validation before merge)
- Discussion: `docs/_inbox/discussion-001.md` (validators, Section 5)
- Current build validation: `task build` / `task test` in Taskfile
