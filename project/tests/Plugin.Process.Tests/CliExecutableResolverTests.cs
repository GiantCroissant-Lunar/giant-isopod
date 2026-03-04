using GiantIsopod.Plugin.Process;
using Xunit;

namespace GiantIsopod.Plugin.Process.Tests;

public sealed class CliExecutableResolverTests
{
    [Fact]
    public void ResolveFromRepoLocalCandidates_FindsSidecarInCommonCheckoutForWorktree()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"giant-isopod-cli-resolver-{Guid.NewGuid():N}");
        var mainCheckout = Path.Combine(tempRoot, "main-checkout");
        var worktreeCheckout = Path.Combine(tempRoot, "feature-worktree");
        Directory.CreateDirectory(mainCheckout);
        Directory.CreateDirectory(worktreeCheckout);

        var previousCurrentDirectory = Directory.GetCurrentDirectory();

        try
        {
            var mainGitDir = Path.Combine(mainCheckout, ".git");
            Directory.CreateDirectory(Path.Combine(mainGitDir, "worktrees", "feature-worktree"));

            var worktreeGitFile = Path.Combine(worktreeCheckout, ".git");
            File.WriteAllText(
                worktreeGitFile,
                $"gitdir: {Path.Combine(mainGitDir, "worktrees", "feature-worktree")}{Environment.NewLine}");

            var executablePath = CreateRepoLocalSidecar(mainCheckout);

            Directory.SetCurrentDirectory(worktreeCheckout);

            var resolved = CliExecutableResolver.ResolveFromRepoLocalCandidates(
                CliExecutableResolver.SidecarRepoLocalCandidates("memory-sidecar"));

            Assert.Equal(executablePath, resolved);
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCurrentDirectory);
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string CreateRepoLocalSidecar(string checkoutRoot)
    {
        if (OperatingSystem.IsWindows())
        {
            var scriptsDir = Path.Combine(checkoutRoot, "memory-sidecar", ".venv", "Scripts");
            Directory.CreateDirectory(scriptsDir);
            var windowsExecutablePath = Path.Combine(scriptsDir, "memory-sidecar.exe");
            File.WriteAllText(windowsExecutablePath, string.Empty);
            return windowsExecutablePath;
        }

        var binDir = Path.Combine(checkoutRoot, "memory-sidecar", ".venv", "bin");
        Directory.CreateDirectory(binDir);
        var unixExecutablePath = Path.Combine(binDir, "memory-sidecar");
        File.WriteAllText(unixExecutablePath, string.Empty);
        return unixExecutablePath;
    }
}
