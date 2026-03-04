using Akka.Actor;
using Akka.TestKit.Xunit2;
using GiantIsopod.Contracts.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GiantIsopod.Plugin.Actors.Tests;

public class WorkspaceActorTests : TestKit, IDisposable
{
    private readonly string _tempRepoPath;
    private readonly IActorRef _workspace;

    public WorkspaceActorTests()
    {
        _tempRepoPath = Path.Combine(Path.GetTempPath(), $"ws-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRepoPath);

        // Initialize a bare-minimum git repo with one commit
        RunGit(_tempRepoPath, "init");
        RunGit(_tempRepoPath, "checkout", "-b", "main");
        File.WriteAllText(Path.Combine(_tempRepoPath, "README.md"), "# test repo\n");
        RunGit(_tempRepoPath, "add", ".");
        RunGit(_tempRepoPath, "commit", "-m", "initial commit");

        _workspace = Sys.ActorOf(Props.Create(() =>
            new WorkspaceActor(
                _tempRepoPath, "main",
                NullLogger<WorkspaceActor>.Instance)));
    }

    public new void Dispose()
    {
        base.Dispose();
        try { Directory.Delete(_tempRepoPath, true); }
        catch { /* best effort cleanup */ }
    }

    [Fact]
    public void Allocate_CreatesWorktreeAndBranch()
    {
        _workspace.Tell(new AllocateWorkspace("T-001", "HEAD"), TestActor);

        var result = ExpectMsg<WorkspaceAllocated>(TimeSpan.FromSeconds(10));
        Assert.Equal("T-001", result.TaskId);
        Assert.StartsWith("swarm/T-001-", result.BranchName, StringComparison.Ordinal);
        Assert.True(Directory.Exists(result.WorktreePath), $"Worktree directory should exist at {result.WorktreePath}");

        // Verify the branch exists
        var branches = RunGit(_tempRepoPath, "branch", "--list", result.BranchName);
        Assert.Contains(result.BranchName, branches);
    }

    [Fact]
    public void Allocate_DuplicateTaskId_ReusesExistingWorkspace()
    {
        _workspace.Tell(new AllocateWorkspace("T-DUP", "HEAD"), TestActor);
        var first = ExpectMsg<WorkspaceAllocated>(TimeSpan.FromSeconds(10));

        _workspace.Tell(new AllocateWorkspace("T-DUP", "HEAD"), TestActor);
        var second = ExpectMsg<WorkspaceAllocated>(TimeSpan.FromSeconds(5));
        Assert.Equal("T-DUP", second.TaskId);
        Assert.Equal(first.WorktreePath, second.WorktreePath);
        Assert.Equal(first.BranchName, second.BranchName);
    }

    [Fact]
    public void Allocate_HierarchicalTaskId_CreatesSafeWorktreeAndBranch()
    {
        _workspace.Tell(new AllocateWorkspace("planner-docs-root/sub-0", "HEAD"), TestActor);

        var result = ExpectMsg<WorkspaceAllocated>(TimeSpan.FromSeconds(10));
        Assert.Equal("planner-docs-root/sub-0", result.TaskId);
        Assert.True(Directory.Exists(result.WorktreePath), $"Worktree directory should exist at {result.WorktreePath}");
        Assert.DoesNotContain("/sub-0", result.WorktreePath, StringComparison.Ordinal);
        Assert.StartsWith("swarm/planner-docs-root-sub-0-", result.BranchName, StringComparison.Ordinal);

        var branches = RunGit(_tempRepoPath, "branch", "--list", result.BranchName);
        Assert.Contains(result.BranchName, branches);
    }

    [Fact]
    public void Release_RemovesWorktreeAndBranch()
    {
        _workspace.Tell(new AllocateWorkspace("T-REL", "HEAD"), TestActor);
        var allocated = ExpectMsg<WorkspaceAllocated>(TimeSpan.FromSeconds(10));
        var worktreePath = allocated.WorktreePath;

        _workspace.Tell(new ReleaseWorkspace("T-REL"), TestActor);
        ExpectMsg<WorkspaceReleased>(TimeSpan.FromSeconds(10));

        Assert.False(Directory.Exists(worktreePath), "Worktree directory should be removed");

        var branches = RunGit(_tempRepoPath, "branch", "--list", allocated.BranchName);
        Assert.DoesNotContain(allocated.BranchName, branches);
    }

    [Fact]
    public void Release_UnknownTaskId_Ignored()
    {
        _workspace.Tell(new ReleaseWorkspace("T-UNKNOWN"), TestActor);
        var released = ExpectMsg<WorkspaceReleased>(TimeSpan.FromSeconds(5));
        Assert.Equal("T-UNKNOWN", released.TaskId);
    }

    [Fact]
    public void MergeQueue_SequentialMerge_Succeeds()
    {
        // Allocate two workspaces
        _workspace.Tell(new AllocateWorkspace("T-M1", "HEAD"), TestActor);
        var ws1 = ExpectMsg<WorkspaceAllocated>(TimeSpan.FromSeconds(10));

        _workspace.Tell(new AllocateWorkspace("T-M2", "HEAD"), TestActor);
        var ws2 = ExpectMsg<WorkspaceAllocated>(TimeSpan.FromSeconds(10));

        // Commit changes in each worktree (different files to avoid conflicts)
        File.WriteAllText(Path.Combine(ws1.WorktreePath, "file1.txt"), "from T-M1\n");
        RunGit(ws1.WorktreePath, "add", "file1.txt");
        RunGit(ws1.WorktreePath, "commit", "-m", "add file1");

        File.WriteAllText(Path.Combine(ws2.WorktreePath, "file2.txt"), "from T-M2\n");
        RunGit(ws2.WorktreePath, "add", "file2.txt");
        RunGit(ws2.WorktreePath, "commit", "-m", "add file2");

        // Request merge for both
        _workspace.Tell(new RequestMerge("T-M1"), TestActor);
        _workspace.Tell(new RequestMerge("T-M2"), TestActor);

        // Both should succeed sequentially
        var results = new List<object>();
        results.Add(ExpectMsg<MergeSucceeded>(TimeSpan.FromSeconds(15)));
        results.Add(ExpectMsg<MergeSucceeded>(TimeSpan.FromSeconds(15)));

        var merged1 = results.OfType<MergeSucceeded>().First(m => m.TaskId == "T-M1");
        var merged2 = results.OfType<MergeSucceeded>().First(m => m.TaskId == "T-M2");
        Assert.NotEmpty(merged1.MergeCommitSha);
        Assert.NotEmpty(merged2.MergeCommitSha);

        // Verify both files exist on main
        RunGit(_tempRepoPath, "checkout", "main");
        Assert.True(File.Exists(Path.Combine(_tempRepoPath, "file1.txt")));
        Assert.True(File.Exists(Path.Combine(_tempRepoPath, "file2.txt")));
    }

    [Fact]
    public void MergeQueue_ConflictDetected()
    {
        // Allocate two workspaces
        _workspace.Tell(new AllocateWorkspace("T-C1", "HEAD"), TestActor);
        var ws1 = ExpectMsg<WorkspaceAllocated>(TimeSpan.FromSeconds(10));

        _workspace.Tell(new AllocateWorkspace("T-C2", "HEAD"), TestActor);
        var ws2 = ExpectMsg<WorkspaceAllocated>(TimeSpan.FromSeconds(10));

        // Both edit the same file with different content
        File.WriteAllText(Path.Combine(ws1.WorktreePath, "shared.txt"), "version from T-C1\n");
        RunGit(ws1.WorktreePath, "add", "shared.txt");
        RunGit(ws1.WorktreePath, "commit", "-m", "edit shared from C1");

        File.WriteAllText(Path.Combine(ws2.WorktreePath, "shared.txt"), "version from T-C2\n");
        RunGit(ws2.WorktreePath, "add", "shared.txt");
        RunGit(ws2.WorktreePath, "commit", "-m", "edit shared from C2");

        // First merge succeeds
        _workspace.Tell(new RequestMerge("T-C1"), TestActor);
        var merged = ExpectMsg<MergeSucceeded>(TimeSpan.FromSeconds(15));
        Assert.Equal("T-C1", merged.TaskId);

        // Second merge should conflict (same file, different content)
        _workspace.Tell(new RequestMerge("T-C2"), TestActor);
        var conflict = ExpectMsg<MergeConflict>(TimeSpan.FromSeconds(15));
        Assert.Equal("T-C2", conflict.TaskId);
    }

    [Fact]
    public void MergeQueue_AutoCommitsDirtyWorktreeBeforeMerge()
    {
        _workspace.Tell(new AllocateWorkspace("T-AUTO", "HEAD"), TestActor);
        var ws = ExpectMsg<WorkspaceAllocated>(TimeSpan.FromSeconds(10));

        File.WriteAllText(Path.Combine(ws.WorktreePath, "auto.txt"), "auto-commit me\n");

        _workspace.Tell(new RequestMerge("T-AUTO"), TestActor);
        var merged = ExpectMsg<MergeSucceeded>(TimeSpan.FromSeconds(15));

        Assert.Equal("T-AUTO", merged.TaskId);

        RunGit(_tempRepoPath, "checkout", "main");
        Assert.True(File.Exists(Path.Combine(_tempRepoPath, "auto.txt")));

        var log = RunGit(_tempRepoPath, "log", "--format=%s", "-n", "1");
        Assert.Contains("task(T-AUTO): agent changes", log);
    }

    [Fact]
    public void MergeQueue_UntrackedAnchorFileDoesNotBlockMerge()
    {
        _workspace.Tell(new AllocateWorkspace("T-UNTRACKED", "HEAD"), TestActor);
        var ws = ExpectMsg<WorkspaceAllocated>(TimeSpan.FromSeconds(10));

        var worktreeTarget = Path.Combine(ws.WorktreePath, "src", "Feature.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(worktreeTarget)!);
        File.WriteAllText(worktreeTarget, "public static class Feature { }\n");

        var anchorConflictPath = Path.Combine(_tempRepoPath, "src", "Feature.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(anchorConflictPath)!);
        File.WriteAllText(anchorConflictPath, "// untracked anchor content\n");

        _workspace.Tell(new RequestMerge("T-UNTRACKED"), TestActor);
        var merged = ExpectMsg<MergeSucceeded>(TimeSpan.FromSeconds(15));

        Assert.Equal("T-UNTRACKED", merged.TaskId);
        Assert.Contains("public static class Feature { }", File.ReadAllText(anchorConflictPath), StringComparison.Ordinal);

        var status = RunGit(_tempRepoPath, "status", "--short");
        Assert.DoesNotContain("?? src/Feature.cs", status, StringComparison.Ordinal);
    }

    private static string RunGit(string workDir, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = System.Diagnostics.Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(30_000);
        return output;
    }
}
