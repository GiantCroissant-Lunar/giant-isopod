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
        var roots = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        }.Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            foreach (var directory in EnumerateWithParents(root))
            {
                foreach (var relativePath in relativeCandidates)
                {
                    var fullPath = Path.Combine(directory, relativePath);
                    if (File.Exists(fullPath))
                        return fullPath;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateWithParents(string start)
    {
        var current = new DirectoryInfo(Path.GetFullPath(start));
        while (current != null)
        {
            yield return current.FullName;
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
