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
    // BaseRef must not start with '-' to prevent git option injection
    private static readonly Regex SafeBaseRefRegex = new(@"^[A-Za-z0-9._/][A-Za-z0-9._/\-]*$", RegexOptions.Compiled);
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
                {
                    _workspaces.Remove(result.TaskId);
                    result.Requester.Tell(new WorkspaceReleased(result.TaskId));
                }
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

        if (_workspaces.TryGetValue(msg.TaskId, out var existing))
        {
            if (Directory.Exists(existing.Workspace.WorktreePath))
            {
                _workspaces[msg.TaskId] = existing with { AllocatedAt = DateTimeOffset.UtcNow };
                _logger.LogDebug("Reusing existing workspace for task {TaskId} at {Path}",
                    msg.TaskId, existing.Workspace.WorktreePath);
                sender.Tell(new WorkspaceAllocated(
                    msg.TaskId,
                    existing.Workspace.WorktreePath,
                    existing.Workspace.BranchName));
                return;
            }

            _logger.LogWarning("Workspace entry for task {TaskId} pointed to missing path {Path}; recreating",
                msg.TaskId, existing.Workspace.WorktreePath);
            _workspaces.Remove(msg.TaskId);
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

        if (string.IsNullOrWhiteSpace(msg.BaseRef) || !SafeBaseRefRegex.IsMatch(msg.BaseRef))
        {
            sender.Tell(new AllocationFailed(msg.TaskId, "Invalid base ref"));
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

        if (!_workspaces.TryGetValue(msg.TaskId, out var ws))
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
        HiddenUntrackedFiles? hiddenFiles = null;
        try
        {
            var commit = await EnsureCommittedAsync(taskId, ws);
            if (!commit.Success)
            {
                return new MergeResult(taskId, Success: false, Sha: null,
                    ConflictFiles: new[] { commit.Error ?? "Failed to commit workspace changes" },
                    Requester: requester);
            }

            // Rebase inside the worktree directory (branch is checked out there, not in anchor)
            var rebase = await RunGitInAsync(ws.WorktreePath, "rebase", _integrationBranch);
            if (rebase.ExitCode != 0)
            {
                await RunGitInAsync(ws.WorktreePath, "rebase", "--abort");
                var conflictFiles = ParseConflictFiles(rebase.Stderr);
                return new MergeResult(taskId, Success: false, Sha: null, ConflictFiles: conflictFiles, Requester: requester);
            }

            hiddenFiles = await HideConflictingUntrackedFilesAsync(taskId, ws);
            if (!hiddenFiles.Success)
            {
                return new MergeResult(taskId, Success: false, Sha: null,
                    ConflictFiles: new[] { hiddenFiles.Error ?? "Failed to prepare anchor worktree for merge" },
                    Requester: requester);
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
            if (rev.ExitCode != 0 || string.IsNullOrWhiteSpace(sha))
            {
                return new MergeResult(taskId, Success: false, Sha: null,
                    ConflictFiles: new[] { $"Failed to resolve merge SHA: {rev.Stderr}" },
                    Requester: requester);
            }

            return new MergeResult(taskId, Success: true, Sha: sha, ConflictFiles: null, Requester: requester);
        }
        catch (Exception ex)
        {
            return new MergeResult(taskId, Success: false, Sha: null,
                ConflictFiles: new[] { ex.Message }, Requester: requester);
        }
        finally
        {
            if (hiddenFiles is not null)
                await RestoreHiddenUntrackedFilesAsync(hiddenFiles);
        }
    }

    private async Task<(bool Success, string? Error)> EnsureCommittedAsync(string taskId, Workspace ws)
    {
        var status = await RunGitInAsync(ws.WorktreePath, "status", "--porcelain");
        if (status.ExitCode != 0)
            return (false, $"Failed to inspect workspace changes: {status.Stderr}");

        if (string.IsNullOrWhiteSpace(status.Stdout))
            return (true, null);

        var add = await RunGitInAsync(ws.WorktreePath, "add", "-A");
        if (add.ExitCode != 0)
            return (false, $"Failed to stage workspace changes: {add.Stderr}");

        var commit = await RunGitInAsync(
            ws.WorktreePath,
            "-c", "user.name=Giant Isopod",
            "-c", "user.email=giant-isopod@local",
            "commit", "-m", $"task({taskId}): agent changes");
        if (commit.ExitCode != 0)
            return (false, $"Failed to commit workspace changes: {commit.Stderr}");

        return (true, null);
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
            result.Requester.Tell(new MergeSucceeded(result.TaskId, result.Sha ?? string.Empty));
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
        // Try safe delete first; fall back to force-delete only if needed (e.g., conflict release)
        var deleteBranch = await RunGitAsync("branch", "-d", ws.BranchName);
        if (deleteBranch.ExitCode != 0)
            deleteBranch = await RunGitAsync("branch", "-D", ws.BranchName);
        var prune = await RunGitAsync("worktree", "prune");
        var ok = remove.ExitCode == 0 && deleteBranch.ExitCode == 0 && prune.ExitCode == 0;
        var err = ok ? null : $"{remove.Stderr} {deleteBranch.Stderr} {prune.Stderr}".Trim();
        return (ok, err);
    }

    private async Task<HiddenUntrackedFiles> HideConflictingUntrackedFilesAsync(string taskId, Workspace ws)
    {
        var incomingPathsResult = await RunGitAsync("diff", "--name-only", $"{_integrationBranch}..{ws.BranchName}");
        if (incomingPathsResult.ExitCode != 0)
            return new HiddenUntrackedFiles(false, Error: $"Failed to inspect incoming merge paths: {incomingPathsResult.Stderr}");

        var incomingPaths = incomingPathsResult.Stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (incomingPaths.Length == 0)
            return new HiddenUntrackedFiles(true, Array.Empty<HiddenUntrackedFile>());

        var untrackedArgs = new List<string> { "ls-files", "--others", "--exclude-standard", "--" };
        untrackedArgs.AddRange(incomingPaths);
        var untrackedResult = await RunGitAsync(untrackedArgs.ToArray());
        if (untrackedResult.ExitCode != 0)
            return new HiddenUntrackedFiles(false, Error: $"Failed to inspect untracked paths: {untrackedResult.Stderr}");

        var conflictingPaths = untrackedResult.Stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (conflictingPaths.Length == 0)
            return new HiddenUntrackedFiles(true, Array.Empty<HiddenUntrackedFile>());

        var backupRoot = Path.Combine(_anchorRepoPath, ".worktrees", ".merge-backups", taskId);
        Directory.CreateDirectory(backupRoot);

        var hidden = new List<HiddenUntrackedFile>(conflictingPaths.Length);
        try
        {
            foreach (var relativePath in conflictingPaths)
            {
                var sourcePath = Path.GetFullPath(Path.Combine(_anchorRepoPath, relativePath));
                if (!sourcePath.StartsWith(_anchorRepoPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return new HiddenUntrackedFiles(false, Error: $"Refused to move unsafe untracked path '{relativePath}'.");

                if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
                    continue;

                var backupPath = Path.Combine(backupRoot, relativePath);
                var backupDir = Path.GetDirectoryName(backupPath);
                if (!string.IsNullOrWhiteSpace(backupDir))
                    Directory.CreateDirectory(backupDir);

                if (File.Exists(sourcePath))
                    File.Move(sourcePath, backupPath, overwrite: true);
                else
                    Directory.Move(sourcePath, backupPath);

                hidden.Add(new HiddenUntrackedFile(relativePath, backupPath));
                _logger.LogInformation("Temporarily moved untracked file {Path} out of the anchor worktree for task {TaskId}",
                    relativePath, taskId);
            }
        }
        catch (Exception ex)
        {
            foreach (var moved in hidden)
            {
                var restorePath = Path.Combine(_anchorRepoPath, moved.RelativePath);
                var restoreDir = Path.GetDirectoryName(restorePath);
                if (!string.IsNullOrWhiteSpace(restoreDir))
                    Directory.CreateDirectory(restoreDir);

                if (File.Exists(moved.BackupPath))
                    File.Move(moved.BackupPath, restorePath, overwrite: true);
                else if (Directory.Exists(moved.BackupPath))
                    Directory.Move(moved.BackupPath, restorePath);
            }

            TryDeleteDirectory(backupRoot);
            return new HiddenUntrackedFiles(false, Error: $"Failed to move conflicting untracked files: {ex.Message}");
        }

        return new HiddenUntrackedFiles(true, hidden, backupRoot);
    }

    private async Task RestoreHiddenUntrackedFilesAsync(HiddenUntrackedFiles hidden)
    {
        foreach (var entry in hidden.Files)
        {
            var restorePath = Path.Combine(_anchorRepoPath, entry.RelativePath);
            if (File.Exists(restorePath) || Directory.Exists(restorePath))
                continue;

            var restoreDir = Path.GetDirectoryName(restorePath);
            if (!string.IsNullOrWhiteSpace(restoreDir))
                Directory.CreateDirectory(restoreDir);

            if (File.Exists(entry.BackupPath))
                File.Move(entry.BackupPath, restorePath, overwrite: true);
            else if (Directory.Exists(entry.BackupPath))
                Directory.Move(entry.BackupPath, restorePath);
        }

        if (!string.IsNullOrWhiteSpace(hidden.BackupRoot))
            await Task.Run(() => TryDeleteDirectory(hidden.BackupRoot));
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
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
    private sealed record HiddenUntrackedFiles(
        bool Success,
        IReadOnlyList<HiddenUntrackedFile>? Entries = null,
        string? BackupRoot = null,
        string? Error = null)
    {
        public IReadOnlyList<HiddenUntrackedFile> Files { get; } = Entries ?? Array.Empty<HiddenUntrackedFile>();
    }
    private sealed record HiddenUntrackedFile(string RelativePath, string BackupPath);
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
