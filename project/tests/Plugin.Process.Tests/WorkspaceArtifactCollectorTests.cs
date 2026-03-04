using GiantIsopod.Contracts.Core;
using Xunit;

namespace GiantIsopod.Plugin.Process.Tests;

public class WorkspaceArtifactCollectorTests : IDisposable
{
    private readonly string _repoPath;

    public WorkspaceArtifactCollectorTests()
    {
        _repoPath = Path.Combine(Path.GetTempPath(), $"artifact-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoPath);

        RunGit(_repoPath, "init");
        RunGit(_repoPath, "checkout", "-b", "main");

        File.WriteAllText(Path.Combine(_repoPath, "README.md"), "# test repo\n");
        RunGit(_repoPath, "add", ".");
        RunGit(_repoPath, "-c", "user.name=Test User", "-c", "user.email=test@example.com", "commit", "-m", "initial commit");
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoPath, true); }
        catch { }
    }

    [Fact]
    public async Task CollectAsync_ReturnsTypedArtifactsForDirtyFiles()
    {
        File.WriteAllText(Path.Combine(_repoPath, "README.md"), "# updated\n");
        Directory.CreateDirectory(Path.Combine(_repoPath, "src"));
        File.WriteAllText(Path.Combine(_repoPath, "src", "Program.cs"), "class Program {}\n");
        File.WriteAllText(Path.Combine(_repoPath, "appsettings.json"), "{ }\n");

        var artifacts = await WorkspaceArtifactCollector.CollectAsync(_repoPath, "task-1", "agent-1");

        Assert.Equal(3, artifacts.Count);
        Assert.Contains(artifacts, a => a.Type == ArtifactType.Doc && a.Metadata!["relativePath"] == "README.md");
        Assert.Contains(artifacts, a => a.Type == ArtifactType.Code && a.Metadata!["relativePath"] == "src/Program.cs");
        Assert.Contains(artifacts, a => a.Type == ArtifactType.Config && a.Metadata!["relativePath"] == "appsettings.json");
        Assert.All(artifacts, a => Assert.StartsWith("task-1:", a.ArtifactId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CollectAsync_SkipsIgnoredRelativePaths()
    {
        File.WriteAllText(Path.Combine(_repoPath, "README.md"), "# updated\n");
        Directory.CreateDirectory(Path.Combine(_repoPath, "project", "tools", "RealCliDogfood", "Batches"));
        File.WriteAllText(
            Path.Combine(_repoPath, "project", "tools", "RealCliDogfood", "Batches", "feature.json"),
            "{ }\n");

        var artifacts = await WorkspaceArtifactCollector.CollectAsync(
            _repoPath,
            "task-1",
            "agent-1",
            new[] { "project/tools/RealCliDogfood/Batches/feature.json" });

        Assert.Single(artifacts);
        Assert.Contains(artifacts, a => a.Metadata!["relativePath"] == "README.md");
        Assert.DoesNotContain(
            artifacts,
            a => a.Metadata!["relativePath"] == "project/tools/RealCliDogfood/Batches/feature.json");
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
