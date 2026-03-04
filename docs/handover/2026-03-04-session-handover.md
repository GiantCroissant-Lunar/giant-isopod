# Session Handover - 2026-03-04

## Main state

- Repo: [giant-isopod](/C:/lunar-horse/yokan-projects/giant-isopod)
- Branch: `main`
- HEAD: `a163dd2` `Persist batch memory for cross-run planner checks`
- Working tree on `main`: clean

Recent commits on `main`:
- `a163dd2` `Persist batch memory for cross-run planner checks`
- `8bfc19a` `Make planner feedback retrieval path-aware`
- `380beef` `Resolve sidecar executables from git common checkout`
- `101c60c` `Use task-aware validator prompts for subtasks`
- `47ed18b` `Add planner runtime affinity for subtasks`
- `8063501` `Map Kimi wire events into AG-UI`
- `47c52c7` `Add Kimi wire runtime and agent-file support`

## Runtime roles

Current practical pool:
- `pi`: default executor for implementation, testing, review
- `kimi-wire`: viable bounded-task executor
- `claude-code`: planner/decomposer, optional executor

Still experimental / paused:
- `copilot`
- `gemini`

## What was cleaned up

Removed clean disposable verification worktrees and their local branches:
- `verify/mixed-pi-kimi-wire`
- `verify/mixed-pi-kimi-wire-turn-begin`
- `verify/pi-kimi-wire-compare-kimi-wire`
- `verify/pi-kimi-wire-compare-pi`
- `verify/planner-feedback-context`
- `verify/planner-pi-kimi-wire`
- `verify/planner-pi-kimi-wire-runtime-affinity-2`
- `verify/planner-pi-kimi-wire-runtime-affinity-3`

## Remaining worktrees

Kept longer-lived or dirty worktrees:

1. [giant-isopod-artifact-followup-visibility](/C:/lunar-horse/yokan-projects/giant-isopod-artifact-followup-visibility)
   Branch: `dogfood/artifact-followup-visibility`
   Status: clean

2. [giant-isopod-feature1-baseline](/C:/lunar-horse/yokan-projects/giant-isopod-feature1-baseline)
   Branch: `dogfood/feature1-baseline`
   Status: clean

3. [giant-isopod-planner-batch-clean](/C:/lunar-horse/yokan-projects/giant-isopod-planner-batch-clean)
   Branch: `verify/planner-batch-clean`
   Status:
   - modified: [010-agent-middleware-pipeline.md](/C:/lunar-horse/yokan-projects/giant-isopod-planner-batch-clean/docs/decisions/010-agent-middleware-pipeline.md)
   - untracked: [feature-planner-batch-docs.json](/C:/lunar-horse/yokan-projects/giant-isopod-planner-batch-clean/project/tools/RealCliDogfood/Batches/feature-planner-batch-docs.json)

4. [giant-isopod-planner-batch-synthesis-fix](/C:/lunar-horse/yokan-projects/giant-isopod-planner-batch-synthesis-fix)
   Branch: `verify/planner-batch-synthesis-fix`
   Status:
   - modified: [AgentActor.cs](/C:/lunar-horse/yokan-projects/giant-isopod-planner-batch-synthesis-fix/project/plugins/Plugin.Actors/AgentActor.cs)
   - modified: [AgentRuntimeActor.cs](/C:/lunar-horse/yokan-projects/giant-isopod-planner-batch-synthesis-fix/project/plugins/Plugin.Actors/AgentRuntimeActor.cs)
   - modified: [PromptBuilder.cs](/C:/lunar-horse/yokan-projects/giant-isopod-planner-batch-synthesis-fix/project/plugins/Plugin.Actors/PromptBuilder.cs)
   - modified: [TaskGraphActor.cs](/C:/lunar-horse/yokan-projects/giant-isopod-planner-batch-synthesis-fix/project/plugins/Plugin.Actors/TaskGraphActor.cs)
   - untracked: [feature-planner-batch-docs.json](/C:/lunar-horse/yokan-projects/giant-isopod-planner-batch-synthesis-fix/project/tools/RealCliDogfood/Batches/feature-planner-batch-docs.json)

5. [giant-isopod-sidecar-common-dir-verify](/C:/lunar-horse/yokan-projects/giant-isopod-sidecar-common-dir-verify)
   Branch: `verify/sidecar-common-dir`
   Status: clean

6. [giant-isopod-sidecar-resolution](/C:/lunar-horse/yokan-projects/giant-isopod-sidecar-resolution)
   Branch: `swarm/sidecar-resolution`
   Status:
   - modified: [Program.cs](/C:/lunar-horse/yokan-projects/giant-isopod-sidecar-resolution/project/tools/RealCliDogfood/Program.cs)
   - untracked directory: [\.worktrees](/C:/lunar-horse/yokan-projects/giant-isopod-sidecar-resolution/.worktrees)
   - untracked: [feature-sidecar-resolution.json](/C:/lunar-horse/yokan-projects/giant-isopod-sidecar-resolution/project/tools/RealCliDogfood/Batches/feature-sidecar-resolution.json)

7. [giant-isopod-sidecar-resolution/.worktrees/sidecar-resolver-tests-8ebe3b7e9e](/C:/lunar-horse/yokan-projects/giant-isopod-sidecar-resolution/.worktrees/sidecar-resolver-tests-8ebe3b7e9e)
   Branch: `swarm/sidecar-resolver-tests-8ebe3b7e9e`
   Status:
   - modified: [MemorySidecarClientTests.cs](/C:/lunar-horse/yokan-projects/giant-isopod-sidecar-resolution/.worktrees/sidecar-resolver-tests-8ebe3b7e9e/project/tests/Plugin.Process.Tests/MemorySidecarClientTests.cs)
   - untracked: [CliExecutableResolverTests.cs](/C:/lunar-horse/yokan-projects/giant-isopod-sidecar-resolution/.worktrees/sidecar-resolver-tests-8ebe3b7e9e/project/tests/Plugin.Process.Tests/CliExecutableResolverTests.cs)

8. [giant-isopod-skills-terminology](/C:/lunar-horse/yokan-projects/giant-isopod-skills-terminology)
   Branch: `swarm/skills-terminology`
   Status:
   - modified: [PromptBuilder.cs](/C:/lunar-horse/yokan-projects/giant-isopod-skills-terminology/project/plugins/Plugin.Actors/PromptBuilder.cs)
   - untracked: [feature-skills-terminology.json](/C:/lunar-horse/yokan-projects/giant-isopod-skills-terminology/project/tools/RealCliDogfood/Batches/feature-skills-terminology.json)

## Current platform status

What is working:
- task worktrees and merges
- runtime-aware dispatch
- planner/executor separation
- planner-generated `preferred_runtime_id`
- AG-UI task graph lifecycle events
- `kimi-wire` transport plus agent-file support
- mixed `pi` + `kimi-wire` execution on bounded tasks
- sidecar executable resolution from isolated outer worktrees

What is still weak:
- real `task-planner` pitfall retrieval is still poor
- agent outcome memory is being reused more effectively than planner pitfall knowledge
- planner reranking now exists, but it only helps after the sidecar returns candidate planner entries

## Latest verification result

Cross-run batch memory persistence now works via `GIANT_ISOPOD_BATCH_MEMORY_DIR`.

However, the most recent planner-learning check showed:
- `task-planner` queries still often return `0` entries before planner dispatch
- a later rerun reused prior knowledge through agent outcome memory
- the next real fix is explicit tag-aware knowledge retrieval in the sidecar/client path, not more prompt-side reranking

## Recommended next steps

1. Add explicit tag-aware knowledge retrieval for planner memory.
   Target files:
   - [MemorySidecarClient.cs](/C:/lunar-horse/yokan-projects/giant-isopod/project/plugins/Plugin.Process/MemorySidecarClient.cs)
   - [KnowledgeStoreActor.cs](/C:/lunar-horse/yokan-projects/giant-isopod/project/plugins/Plugin.Actors/KnowledgeStoreActor.cs)
   - sidecar query path under [memory-sidecar/src/memory_sidecar](/C:/lunar-horse/yokan-projects/giant-isopod/memory-sidecar/src/memory_sidecar)

2. After that, rerun a two-step planner verification:
   - seed a `planning-pitfall`
   - run the same planner batch with the same `GIANT_ISOPOD_BATCH_MEMORY_DIR`
   - confirm the planner prompt actually contains `Planning feedback context:`

3. Only after planner retrieval is real, continue with:
   - stronger planner skill affinity
   - more planner-led mixed `pi` + `kimi-wire` batches

## Notes for next session

- `main` is clean and safe to continue from.
- Do not delete the dirty retained worktrees blindly; they contain unfinished or diagnostic state.
- If you need disposable verification again, create fresh `verify/*` worktrees and remove them when done.
