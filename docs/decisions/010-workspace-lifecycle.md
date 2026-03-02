# ADR-010: Workspace Lifecycle (Worktree-per-Task)

Date: 2026-03-02
Status: Proposed
Depends On: ADR-004, ADR-008

## Context

When multiple agents produce code concurrently, they share a single working directory.
This causes:

1. **File conflicts** — Two agents editing different files in the same checkout can still
   collide on generated files, lockfiles, or build outputs.
2. **Dirty state** — An agent's uncommitted changes pollute the workspace for the next agent.
3. **No isolation** — A failing agent can leave broken state that blocks subsequent tasks.

Git worktrees solve workspace conflicts by giving each task an isolated directory backed by
the same object store. The disk cost is minimal (shared `.git`), and branches map cleanly
to the artifact model (commit SHA = code artifact reference).

Giant-isopod already uses worktrees informally. This ADR formalizes a lifecycle: allocate
a worktree when a code task is awarded, enforce clean commits, and release/merge when done.

## Decision

Introduce a `WorkspaceActor` that manages the lifecycle of git worktrees. Each code-producing
task gets an isolated worktree. The orchestrator controls merges via a sequential merge queue.
Non-code tasks (image generation, external API calls) skip worktree allocation.

## Design

### Workspace Model

```csharp
public record Workspace(
    string TaskId,
    string WorktreePath,    // e.g. ".worktrees/T-042/"
    string BranchName,      // e.g. "swarm/T-042"
    string BaseRef,         // commit SHA the worktree branched from
    WorkspaceStatus Status
);

public enum WorkspaceStatus
{
    Active,       // agent is working in it
    Committed,    // agent committed, awaiting merge
    Merged,       // merged into integration branch
    Released      // worktree removed, branch deleted
}
```

### Messages

```csharp
// ── Allocation ──
public record AllocateWorkspace(string TaskId, string BaseRef);
public record WorkspaceAllocated(string TaskId, string WorktreePath, string BranchName);
public record AllocationFailed(string TaskId, string Reason);

// ── Release ──
public record ReleaseWorkspace(string TaskId);
public record WorkspaceReleased(string TaskId);

// ── Merge queue ──
public record RequestMerge(string TaskId);
public record MergeSucceeded(string TaskId, string MergeCommitSha);
public record MergeConflict(string TaskId, IReadOnlyList<string> ConflictingFiles);
```

### WorkspaceActor

```
/user/workspace — WorkspaceActor
    State:
      Dictionary<TaskId, Workspace> active workspaces
      Queue<TaskId> merge queue (FIFO)
      string anchorRepoPath (main checkout, never modified by agents)
      string integrationBranch (default: "main")

    OnReceive:
      AllocateWorkspace →
        git worktree add .worktrees/{TaskId} -b swarm/{TaskId} {BaseRef}
        reply WorkspaceAllocated

      ReleaseWorkspace →
        git worktree remove .worktrees/{TaskId}
        git branch -D swarm/{TaskId}
        git worktree prune
        reply WorkspaceReleased

      RequestMerge →
        enqueue TaskId
        process queue head:
          git fetch, rebase onto integration branch
          if clean: fast-forward merge, reply MergeSucceeded
          if conflict: reply MergeConflict, dequeue (spawn conflict-resolution task)
```

### Integration with Task Flow

When `DispatchActor` awards a code task:

```
1. DispatchActor sends TaskAwardedTo(taskId, agentId)
2. DispatchActor sends AllocateWorkspace(taskId, baseRef: HEAD) to WorkspaceActor
3. WorkspaceActor creates worktree, replies WorkspaceAllocated
4. DispatchActor sends TaskAssigned(taskId, ..., workspacePath) to AgentActor
5. Agent works exclusively inside workspacePath
6. Agent commits to swarm/{taskId} branch
7. Agent sends TaskCompleted with code artifact (gitref: commit SHA)
8. TaskGraphActor sends RequestMerge to WorkspaceActor
9. On MergeSucceeded → register merged artifact, release workspace
   On MergeConflict → spawn conflict-resolution task (ADR-009)
```

### Merge Queue Strategy

Merges are **sequential** (one at a time) to avoid compound conflicts:

```
Queue: [T-042, T-043, T-044]

1. Rebase swarm/T-042 onto main → merge → advance main
2. Rebase swarm/T-043 onto main (now includes T-042) → merge → advance main
3. Rebase swarm/T-044 onto main (now includes T-042+T-043) → merge → advance main
```

If step 2 conflicts, T-043 gets a `MergeConflict`. The orchestrator can:
- Spawn a conflict-resolution task (new worktree, receives both branches).
- Re-queue after resolution.
- Skip and move to T-044 (if independent).

### Conflict Avoidance: Area Hints

To reduce conflicts proactively, `TaskAvailable` includes optional area hints:

```csharp
public record TaskAvailable(
    string TaskId,
    string Description,
    IReadOnlySet<string> RequiredCapabilities,
    TimeSpan BidWindow,
    IReadOnlySet<string>? FileAreaHints  // NEW: e.g. {"src/Actors/", "tests/"}
);
```

`DispatchActor` can serialize tasks with overlapping area hints instead of running them
in parallel. Simple heuristic: if `FileAreaHints` intersect, serialize. If disjoint, parallel.

### Agent Workspace Rules

Agents working in a worktree must follow:

1. **Work only inside the worktree path** — Never touch the anchor checkout.
2. **Commit before completing** — `git status` must be clean. Uncommitted changes = task failure.
3. **Follow commit conventions** — Conventional Commits (ADR project rules).
4. **Include diff artifact** — Attach `git diff {baseRef}...HEAD` as a patch artifact for review.

### Non-Code Tasks

Tasks that don't modify repo files (image generation, audio synthesis, API calls) skip
worktree allocation entirely. Their artifacts go to external storage with URIs registered
in the artifact registry (ADR-008). If a non-code artifact later needs to be committed to
the repo (e.g., importing a generated texture), a separate "import" code task handles it
through a worktree.

## Trade-offs

### Why worktrees (not separate clones)

- Shared `.git` object store — N worktrees cost ~N× working tree size, not N× full repo.
- Atomic branch operations — All branches visible from any worktree.
- Standard git tooling — No custom VCS layer needed.

### Why sequential merge queue (not parallel merge)

- Parallel merges create compound conflicts that are harder to resolve than sequential ones.
- Sequential is slower but deterministic — order is FIFO, results are reproducible.
- For the expected parallelism (5–15 concurrent agents), sequential merge latency is
  acceptable (seconds per merge).

### Why not file locking

- File-level locking is fragile, requires tracking which files each task will touch (often
  unknown upfront), and doesn't compose well with git.
- Area hints are a softer mechanism: they guide scheduling without hard locks.

### Risk

- **Worktree accumulation** — If tasks fail without releasing, worktrees pile up.
  Mitigate with a periodic cleanup sweep (stale worktrees older than 1 hour → force release).
- **Rebase failures on large divergence** — If a task takes very long, its branch diverges
  significantly from main. Mitigate with periodic rebase during execution (optional,
  agent-triggered).
- **Windows path length** — Deep worktree paths can hit Windows MAX_PATH. Mitigate by
  keeping worktree names short (task ID only, no nested directories).

## References

- ADR-004: Market-First Coordination (TaskAwardedTo flow)
- ADR-008: Artifact Registry (code artifacts as git refs)
- ADR-009: Progressive Task Decomposition (conflict-resolution as subtask)
- Discussion: `docs/_inbox/discussion-001.md` (worktree integration, Section on git)
- Existing worktree usage: `.claude/worktrees/` (informal, to be formalized)
