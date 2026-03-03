# Dogfood Checklist

Use this checklist before relying on a runtime for real code-edit tasks.

## Acceptance Bar

A runtime is dogfood-ready for the core development workflow only if all of these pass:

1. `basic` smoke:
   `dotnet run --project project\tools\RealCliSmoke\RealCliSmoke.csproj -- <runtime> 8 basic`
2. `review-pass` smoke:
   `dotnet run --project project\tools\RealCliSmoke\RealCliSmoke.csproj -- <runtime> 8 review-pass`
3. `review-fail` smoke:
   `dotnet run --project project\tools\RealCliSmoke\RealCliSmoke.csproj -- <runtime> 8 review-fail`
4. The runtime stays inside the task worktree on retries.
5. The final transcript contains a valid `<giant-isopod-result>` envelope.
6. `review-pass` merges exactly the expected file.
7. `review-fail` does not merge into `main`.

## Current Targets

- `pi`: required
- `kimi`: required

## Failure Buckets

Classify failures into one bucket before changing code:

- `launch`: runtime executable, args, env, or encoding failure
- `prompt`: runtime ignored or misread the task contract
- `envelope`: runtime edited files but did not return a parseable result envelope
- `workspace`: task or retry ran outside the assigned worktree
- `artifact`: edits happened but artifact detection/registration was wrong
- `review`: validator or revision loop behaved incorrectly
- `merge`: merge/release behavior was wrong after validation

## Current Priority

1. Make `kimi` pass all three smoke scenarios.
2. Re-run `pi` after any shared runtime or parser changes.
3. Only after both are green, use them for real dogfood tasks.

## Parallel Feature Batch

Use these when the runtime smoke gates are green and the goal is to verify real multi-agent development work, not just isolated smoke tasks.

### Feature 1: Runtime-Aware Dispatch

Goal: let task routing intentionally prefer `pi` or `kimi` instead of relying only on capability and load.

Target files:

- `project/contracts/Contracts.Core/Messages.cs`
- `project/plugins/Plugin.Actors/DispatchActor.cs`
- `project/plugins/Plugin.Actors/TaskGraphActor.cs`
- `project/tests/Plugin.Actors.Tests/DispatchActorTests.cs`
- `project/tools/RealCliParallelSmoke/Program.cs`

Recommended task graph:

1. `dispatch-contract`
   Add task-level runtime preference fields to the contracts and graph request path.
   Preferred runtime: `pi`
2. `dispatch-selection`
   Update dispatch scoring so runtime preference affects bidder selection without breaking fallback behavior.
   Preferred runtime: `pi`
3. `dispatch-tests`
   Add tests for preferred runtime wins, fallback when preferred runtime is unavailable, and mixed-runtime backlog behavior.
   Preferred runtime: `kimi`
4. `dispatch-parallel-smoke`
   Extend the parallel smoke tool to submit mixed-runtime tasks and assert that the preferred runtime handled each task when capacity allowed.
   Preferred runtime: `kimi`
5. `dispatch-review`
   Run a final integration pass across the contract, actor, tests, and smoke harness.
   Preferred runtime: `pi`

Dependencies:

1. `dispatch-contract -> dispatch-selection`
2. `dispatch-contract -> dispatch-tests`
3. `dispatch-selection -> dispatch-parallel-smoke`
4. `dispatch-tests -> dispatch-review`
5. `dispatch-parallel-smoke -> dispatch-review`

Parallelism notes:

- `dispatch-selection` and `dispatch-tests` can run in parallel after `dispatch-contract`.
- `dispatch-review` should be a separate final task, not done by the same agent that owned `dispatch-selection`.

Acceptance bar:

1. Unit tests prove preferred runtime routing and fallback behavior.
2. Mixed-runtime parallel smoke passes with both `pi` and `kimi`.
3. At least one run shows preference honored when both runtimes are idle.
4. Another run shows fallback still succeeds when the preferred runtime is unavailable or saturated.

Concrete task submissions:

1. `dispatch-contract`
   Preferred runtime: `pi`
   Description:
   Add runtime preference support to the task contracts.
   Update `project/contracts/Contracts.Core/Messages.cs` so task nodes and dispatch requests can carry a preferred runtime id.
   Preserve existing behavior when no preferred runtime is specified.
   Edit only the contract file unless a compile fix is strictly required.
2. `dispatch-selection`
   Preferred runtime: `pi`
   Description:
   Update `project/plugins/Plugin.Actors/DispatchActor.cs` so bidder selection prefers agents whose runtime matches the task's preferred runtime id when such bids are available.
   Keep fallback behavior intact when no preferred-runtime bid is available.
   Do not encode runtime preference as a fake capability.
3. `dispatch-tests`
   Preferred runtime: `kimi`
   Description:
   Add or update tests in `project/tests/Plugin.Actors.Tests/DispatchActorTests.cs` to prove:
   preferred runtime wins when available;
   non-preferred runtime still wins when no preferred-runtime bid exists;
   existing duplicate/non-capable bid protections still hold.
   Edit only the dispatch test file unless a compile fix is strictly required.
4. `dispatch-parallel-smoke`
   Preferred runtime: `kimi`
   Description:
   Update `project/tools/RealCliParallelSmoke/Program.cs` so the mixed-runtime smoke submits alternating `pi` and `kimi` preferred tasks and verifies each completed task was handled by the preferred runtime when capacity allowed.
   Keep the existing parallel capacity verification.
5. `dispatch-review`
   Preferred runtime: `pi`
   Description:
   Review the Feature 1 changes for consistency across contracts, dispatch selection, tests, and the mixed-runtime smoke tool.
   Tighten any small inconsistencies needed for the batch to build and verify cleanly.

### Feature 2: Runtime Process Observability

Goal: report the real child CLI process identity instead of the host process ID.

Target files:

- `project/plugins/Plugin.Process/CliAgentRuntime.cs`
- `project/plugins/Plugin.Actors/AgentRuntimeActor.cs`
- `project/contracts/Contracts.Core/Messages.cs`
- `project/tests/Plugin.Process.Tests/*`
- `project/tools/RealCliParallelSmoke/Program.cs`

Recommended split:

1. runtime child-process capture
2. runtime event contract update
3. smoke output verification
4. final review

### Feature 3: Multi-Feature Dogfood Runner

Goal: submit a real DAG with independent code, test, and doc nodes instead of only flat task sets.

Target files:

- `project/tools/RealCliDogfood/Program.cs`
- `project/plugins/Plugin.Actors/TaskGraphActor.cs`
- `DOGFOOD.md`

Recommended split:

1. runner graph-input format
2. runner summary output
3. graph template for code/test/doc tasks
4. final review

### Feature 4: Artifact Follow-Up Workflow

Goal: turn registered artifacts into follow-up tasks such as tests, docs, or review-focused tasks.

Target files:

- `project/plugins/Plugin.Actors/ArtifactRegistryActor.cs`
- `project/plugins/Plugin.Actors/TaskGraphActor.cs`
- `project/plugins/Plugin.Actors/ValidatorActor.cs`
- `project/tests/Plugin.Actors.Tests/*`

Recommended split:

1. artifact classification
2. follow-up task generation
3. validator integration
4. end-to-end dogfood verification

## Execution Order

1. Runtime-aware dispatch
2. Runtime process observability
3. Multi-feature dogfood runner
4. Artifact follow-up workflow

This order is intentional: first teach the system who should do work, then make runtime execution observable, then increase graph complexity, then add artifact-driven follow-up behavior.
