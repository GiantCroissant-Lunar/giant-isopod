using System.Text.RegularExpressions;
using Akka.Actor;
using CliWrap;
using CliWrap.Buffered;
using GiantIsopod.Contracts.Core;
using Microsoft.Extensions.Logging;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// /user/workspace — manages git worktree lifecycle for code tasks (ADR-010).
/// Each code task gets an isolated worktree; merges are serialized via FIFO queue.
/// </summary>
public sealed class WorkspaceActor : UntypedActor, IWithTimers
{
    private readonly string _anchorRepoPath;
    private readonly string _integrationBranch;
    private readonly ILogger<WorkspaceActor> _logger;

    private readonly Dictionary<string, WorkspaceEntry> _workspaces = new();
    private readonly Queue<(string TaskId, IActorRef Requester)> _mergeQueue = new();
    private bool _merging;

    private static readonly Regex SafeTaskIdRegex = new(@"^[A-Za-z0-9._-]+$", RegexOptions.Compiled);
    private static readonly TimeSpan GitCommandTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromHours(1);

    public ITimerScheduler Timers { get; set; } = null!;

    public WorkspaceActor(string anchorRepoPath, string integrationBranch, ILogger<WorkspaceActor> logger)
    {
        _anchorRepoPath = anchorRepoPath;
        _integrationBranch = integrationBranch;
        _logger = logger;
    }

    protected override void PreStart()
    {
        Timers.StartPeriodicTimer("cleanup", CleanupStale.Instance, CleanupInterval, CleanupInterval);
    }

    protected override void OnReceive(object message)
    {
        switch (message)
        {
            case AllocateWorkspace allocate:
                HandleAllocate(allocate);
                break;

            case ReleaseWorkspace release:
                HandleRelease(release);
                break;

            case RequestMerge merge:
                HandleRequestMerge(merge);
                break;

            case AllocateResult result:
                HandleAllocateResult(result);
                break;

            case ReleaseResult result:
                if (result.Success)
                    result.Requester.Tell(new WorkspaceReleased(result.TaskId));
                else
                    _logger.LogWarning("Workspace release failed for task {TaskId}: {Error}", result.TaskId, result.Error);
                break;

            case MergeResult result:
                HandleMergeResult(result);
                break;

            case MergeQueueTick:
                ProcessMergeQueue();
                break;

            case CleanupStale:
                HandleCleanup();
                break;
        }
    }

    private void HandleAllocate(AllocateWorkspace msg)
    {
        var sender = Sender;

        if (_workspaces.ContainsKey(msg.TaskId))
        {
            sender.Tell(new AllocationFailed(msg.TaskId, $"Workspace already exists for task {msg.TaskId}"));
            return;
        }

        if (string.IsNullOrWhiteSpace(msg.TaskId) || !SafeTaskIdRegex.IsMatch(msg.TaskId))
        {
            sender.Tell(new AllocationFailed(msg.TaskId, "Invalid task id"));
            return;
        }

        var worktreesRoot = Path.GetFullPath(Path.Combine(_anchorRepoPath, ".worktrees"));
        var worktreePath = Path.GetFullPath(Path.Combine(worktreesRoot, msg.TaskId));
        if (!worktreePath.StartsWith(worktreesRoot + Path.DirectorySeparatorChar))
        {
            sender.Tell(new AllocationFailed(msg.TaskId, "Invalid task id path"));
            return;
        }

        var branchName = $"swarm/{msg.TaskId}";

        RunGitAsync("worktree", "add", worktreePath, "-b", branchName, msg.BaseRef)
            .ContinueWith(t =>
            {
                var gitResult = t.Result;
                return (object)new AllocateResult(msg.TaskId, worktreePath, branchName, msg.BaseRef,
                    gitResult.ExitCode == 0, gitResult.Stderr, sender);
            })
            .PipeTo(Self);
    }

    private void HandleAllocateResult(AllocateResult result)
    {
        if (result.Success)
        {
            var ws = new WorkspaceEntry(
                new Workspace(result.TaskId, result.WorktreePath, result.BranchName, result.BaseRef, WorkspaceStatus.Active),
                DateTimeOffset.UtcNow);
            _workspaces[result.TaskId] = ws;

            _logger.LogInformation("Workspace allocated for task {TaskId} at {Path}", result.TaskId, result.WorktreePath);
            result.Requester.Tell(new WorkspaceAllocated(result.TaskId, result.WorktreePath, result.BranchName));
        }
        else
        {
            _logger.LogWarning("Workspace allocation failed for task {TaskId}: {Error}", result.TaskId, result.Error);
            result.Requester.Tell(new AllocationFailed(result.TaskId, result.Error ?? "Unknown error"));
        }
    }

    private void HandleRelease(ReleaseWorkspace msg)
    {
        var sender = Sender;

        if (!_workspaces.Remove(msg.TaskId, out var ws))
        {
            sender.Tell(new WorkspaceReleased(msg.TaskId));
            return;
        }

        ReleaseWorktreeAsync(ws.Workspace)
            .ContinueWith(t =>
                (object)new ReleaseResult(
                    msg.TaskId,
                    sender,
                    t.IsCompletedSuccessfully && t.Result.Success,
                    t.IsCompletedSuccessfully ? t.Result.Error : (t.Exception?.GetBaseException()?.Message ?? "Operation cancelled")))
            .PipeTo(Self);
    }

    private void HandleRequestMerge(RequestMerge msg)
    {
        if (!_workspaces.ContainsKey(msg.TaskId))
        {
            _logger.LogWarning("Merge requested for unknown task {TaskId}", msg.TaskId);
            Sender.Tell(new MergeConflict(msg.TaskId, Array.Empty<string>()));
            return;
        }

        _workspaces[msg.TaskId] = _workspaces[msg.TaskId] with
        {
            Workspace = _workspaces[msg.TaskId].Workspace with { Status = WorkspaceStatus.Committed }
        };
        _mergeQueue.Enqueue((msg.TaskId, Sender));
        _logger.LogDebug("Task {TaskId} enqueued for merge (queue depth: {Depth})", msg.TaskId, _mergeQueue.Count);

        if (!_merging)
            Self.Tell(MergeQueueTick.Instance);
    }

    private void ProcessMergeQueue()
    {
        if (_merging || _mergeQueue.Count == 0)
            return;

        var (taskId, requester) = _mergeQueue.Dequeue();

        if (!_workspaces.TryGetValue(taskId, out var ws) || ws.Workspace.Status != WorkspaceStatus.Committed)
        {
            // Already released or in invalid state; try next
            Self.Tell(MergeQueueTick.Instance);
            return;
        }

        _merging = true;

        DoMergeAsync(taskId, ws.Workspace, requester)
            .ContinueWith(t => t.Result)
            .PipeTo(Self);
    }

    private async Task<MergeResult> DoMergeAsync(string taskId, Workspace ws, IActorRef requester)
    {
        try
        {
            // Rebase inside the worktree directory (branch is checked out there, not in anchor)
            var rebase = await RunGitInAsync(ws.WorktreePath, "rebase", _integrationBranch);
            if (rebase.ExitCode != 0)
            {
                await RunGitInAsync(ws.WorktreePath, "rebase", "--abort");
                var conflictFiles = ParseConflictFiles(rebase.Stderr);
                return new MergeResult(taskId, Success: false, Sha: null, ConflictFiles: conflictFiles, Requester: requester);
            }

            // Ensure anchor repo is on integration branch before merging
            var switchResult = await RunGitAsync("switch", _integrationBranch);
            if (switchResult.ExitCode != 0)
            {
                return new MergeResult(taskId, Success: false, Sha: null,
                    ConflictFiles: new[] { $"Failed to switch to {_integrationBranch}: {switchResult.Stderr}" },
                    Requester: requester);
            }

            // Fast-forward merge into integration branch from the anchor repo
            var merge = await RunGitAsync("merge", "--ff-only", ws.BranchName);
            if (merge.ExitCode != 0)
            {
                return new MergeResult(taskId, Success: false, Sha: null,
                    ConflictFiles: new[] { $"Fast-forward merge failed: {merge.Stderr}" },
                    Requester: requester);
            }

            var rev = await RunGitAsync("rev-parse", "HEAD");
            var sha = rev.Stdout.Trim();

            return new MergeResult(taskId, Success: true, Sha: sha, ConflictFiles: null, Requester: requester);
        }
        catch (Exception ex)
        {
            return new MergeResult(taskId, Success: false, Sha: null,
                ConflictFiles: new[] { ex.Message }, Requester: requester);
        }
    }

    private void HandleMergeResult(MergeResult result)
    {
        _merging = false;

        if (result.Success)
        {
            if (_workspaces.TryGetValue(result.TaskId, out var ws))
            {
                _workspaces[result.TaskId] = ws with
                {
                    Workspace = ws.Workspace with { Status = WorkspaceStatus.Merged }
                };
            }

            _logger.LogInformation("Task {TaskId} merged successfully (sha: {Sha})", result.TaskId, result.Sha);
            result.Requester.Tell(new MergeSucceeded(result.TaskId, result.Sha!));
        }
        else
        {
            _logger.LogWarning("Task {TaskId} merge conflict: {Files}",
                result.TaskId, string.Join(", ", result.ConflictFiles ?? Array.Empty<string>()));
            result.Requester.Tell(new MergeConflict(result.TaskId, result.ConflictFiles ?? Array.Empty<string>()));
        }

        // Process next item in queue
        if (_mergeQueue.Count > 0)
            Self.Tell(MergeQueueTick.Instance);
    }

    private void HandleCleanup()
    {
        var now = DateTimeOffset.UtcNow;
        var stale = _workspaces
            .Where(kv => kv.Value.Workspace.Status == WorkspaceStatus.Active
                         && now - kv.Value.AllocatedAt > StaleThreshold)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var taskId in stale)
        {
            _logger.LogWarning("Releasing stale workspace for task {TaskId}", taskId);
            Self.Tell(new ReleaseWorkspace(taskId));
        }
    }

    private async Task<(bool Success, string? Error)> ReleaseWorktreeAsync(Workspace ws)
    {
        var remove = await RunGitAsync("worktree", "remove", "--force", ws.WorktreePath);
        var deleteBranch = await RunGitAsync("branch", "-D", ws.BranchName);
        var prune = await RunGitAsync("worktree", "prune");
        var ok = remove.ExitCode == 0 && deleteBranch.ExitCode == 0 && prune.ExitCode == 0;
        var err = ok ? null : $"{remove.Stderr} {deleteBranch.Stderr} {prune.Stderr}".Trim();
        return (ok, err);
    }

    private Task<GitResult> RunGitAsync(params string[] args)
        => RunGitInAsync(_anchorRepoPath, args);

    private async Task<GitResult> RunGitInAsync(string workDir, params string[] args)
    {
        using var cts = new CancellationTokenSource(GitCommandTimeout);
        var result = await Cli.Wrap("git")
            .WithArguments(args)
            .WithWorkingDirectory(workDir)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cts.Token);

        if (result.ExitCode != 0)
        {
            _logger.LogDebug("git {Args} (in {Dir}) exited {Code}: {Stderr}",
                string.Join(" ", args), workDir, result.ExitCode, result.StandardError);
        }

        return new GitResult(result.ExitCode, result.StandardOutput, result.StandardError);
    }

    private static IReadOnlyList<string> ParseConflictFiles(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return Array.Empty<string>();

        return stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.Contains("CONFLICT") || l.Contains("Merge conflict"))
            .ToList();
    }

    // ── Internal types ──

    internal sealed record WorkspaceEntry(Workspace Workspace, DateTimeOffset AllocatedAt);

    private sealed record AllocateResult(string TaskId, string WorktreePath, string BranchName, string BaseRef, bool Success, string? Error, IActorRef Requester);
    private sealed record ReleaseResult(string TaskId, IActorRef Requester, bool Success, string? Error);
    private sealed record MergeResult(string TaskId, bool Success, string? Sha, IReadOnlyList<string>? ConflictFiles, IActorRef Requester);
    private sealed record MergeQueueTick
    {
        public static readonly MergeQueueTick Instance = new();
    }
    private sealed record CleanupStale
    {
        public static readonly CleanupStale Instance = new();
    }
    private sealed record GitResult(int ExitCode, string Stdout, string Stderr);
}
