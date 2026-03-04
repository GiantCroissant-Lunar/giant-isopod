namespace GiantIsopod.Plugin.Process;

public static class CliExecutableResolver
{
    public static string Resolve(string executable, IEnumerable<string>? repoLocalCandidates = null)
    {
        if (Path.IsPathRooted(executable) ||
            executable.Contains(Path.DirectorySeparatorChar) ||
            executable.Contains(Path.AltDirectorySeparatorChar))
        {
            return executable;
        }

        var fromPath = ResolveFromPath(executable);
        if (fromPath != null)
            return fromPath;

        if (repoLocalCandidates != null)
        {
            var local = ResolveRepoLocal(repoLocalCandidates);
            if (local != null)
                return local;
        }

        return executable;
    }

    public static string? ResolveFromRepoLocalCandidates(IEnumerable<string> repoLocalCandidates)
    {
        return ResolveRepoLocal(repoLocalCandidates);
    }

    public static IEnumerable<string> SidecarRepoLocalCandidates(string executable)
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine("memory-sidecar", ".venv", "Scripts", $"{executable}.exe");
            yield return Path.Combine("memory-sidecar", ".venv", "Scripts", $"{executable}.cmd");
        }
        else
        {
            yield return Path.Combine("memory-sidecar", ".venv", "bin", executable);
        }
    }

    public static string? ResolveFromPathEntries(string executable, IEnumerable<string> pathEntries)
    {
        foreach (var directory in pathEntries)
        {
            foreach (var candidate in EnumerateExecutableCandidates(executable))
            {
                var fullPath = Path.Combine(directory, candidate);
                if (File.Exists(fullPath))
                    return fullPath;
            }
        }

        return null;
    }

    private static string? ResolveFromPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        return ResolveFromPathEntries(executable, path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string? ResolveRepoLocal(IEnumerable<string> relativeCandidates)
    {
        var roots = EnumerateResolutionRoots()
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            string? repoRoot = FindGitRepoRoot(root);
            string? commonCheckoutRoot = repoRoot != null
                ? TryResolveGitCommonCheckoutRoot(repoRoot)
                : null;

            foreach (var directory in EnumerateWithParents(root, repoRoot))
            {
                foreach (var relativePath in relativeCandidates)
                {
                    var fullPath = Path.Combine(directory, relativePath);
                    if (File.Exists(fullPath))
                        return fullPath;
                }
            }

            if (commonCheckoutRoot != null)
            {
                foreach (var relativePath in relativeCandidates)
                {
                    var fullPath = Path.Combine(commonCheckoutRoot, relativePath);
                    if (File.Exists(fullPath))
                        return fullPath;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateResolutionRoots()
    {
        yield return Directory.GetCurrentDirectory();
        yield return AppContext.BaseDirectory;
    }

    private static string? FindGitRepoRoot(string startPath)
    {
        var current = new DirectoryInfo(Path.GetFullPath(startPath));
        while (current != null)
        {
            var gitPath = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        return null;
    }

    private static string? TryResolveGitCommonCheckoutRoot(string repoRoot)
    {
        var gitPath = Path.Combine(repoRoot, ".git");
        if (!File.Exists(gitPath))
            return null;

        string? gitDir = null;
        foreach (var line in File.ReadLines(gitPath))
        {
            const string prefix = "gitdir:";
            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var candidate = line[prefix.Length..].Trim();
            if (string.IsNullOrWhiteSpace(candidate))
                return null;

            gitDir = Path.GetFullPath(Path.IsPathRooted(candidate)
                ? candidate
                : Path.Combine(repoRoot, candidate));
            break;
        }

        if (gitDir == null)
            return null;

        var current = new DirectoryInfo(gitDir);
        while (current != null && !string.Equals(current.Name, ".git", StringComparison.OrdinalIgnoreCase))
            current = current.Parent;

        return current?.Parent?.FullName;
    }

    private static IEnumerable<string> EnumerateWithParents(string start, string? stopAt = null)
    {
        var current = new DirectoryInfo(Path.GetFullPath(start));
        while (current != null)
        {
            yield return current.FullName;
            if (stopAt != null && string.Equals(current.FullName, stopAt, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            current = current.Parent;
        }
    }

    private static IEnumerable<string> EnumerateExecutableCandidates(string executable)
    {
        if (OperatingSystem.IsWindows())
        {
            yield return $"{executable}.exe";
            yield return $"{executable}.cmd";
            yield return $"{executable}.bat";
        }

        yield return executable;
    }
}
