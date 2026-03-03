using System.Security.Cryptography;
using CliWrap;
using CliWrap.Buffered;
using GiantIsopod.Contracts.Core;

namespace GiantIsopod.Plugin.Process;

/// <summary>
/// Converts dirty files in a git workspace into typed ArtifactRef entries.
/// </summary>
public static class WorkspaceArtifactCollector
{
    public static async Task<IReadOnlyList<ArtifactRef>> CollectAsync(
        string workspacePath,
        string taskId,
        string agentId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
            return Array.Empty<ArtifactRef>();

        var relativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in await CollectPathsAsync(workspacePath, ["diff", "--name-only", "--relative", "HEAD"], ct))
            relativePaths.Add(path);
        foreach (var path in await CollectPathsAsync(workspacePath, ["diff", "--cached", "--name-only", "--relative", "HEAD"], ct))
            relativePaths.Add(path);
        foreach (var path in await CollectPathsAsync(workspacePath, ["ls-files", "--others", "--exclude-standard"], ct))
            relativePaths.Add(path);

        if (relativePaths.Count == 0)
            return Array.Empty<ArtifactRef>();

        var createdAt = DateTimeOffset.UtcNow;
        var normalizedWorkspaceRoot = Path.GetFullPath(
            workspacePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            + Path.DirectorySeparatorChar;
        var artifacts = new List<ArtifactRef>(relativePaths.Count);
        foreach (var relativePath in relativePaths)
        {
            var fullPath = Path.GetFullPath(Path.Combine(workspacePath, relativePath));
            if (!fullPath.StartsWith(normalizedWorkspaceRoot, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!File.Exists(fullPath))
                continue;

            var bytes = await File.ReadAllBytesAsync(fullPath, ct);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var sanitizedPath = relativePath.Replace('\\', '/').Replace('/', '_').Replace(':', '_');

            artifacts.Add(new ArtifactRef(
                ArtifactId: $"{taskId}:{sanitizedPath}:{hash[..12]}",
                Type: InferType(relativePath),
                Format: InferFormat(relativePath),
                Uri: fullPath,
                ContentHash: hash,
                Provenance: new ArtifactProvenance(taskId, agentId, createdAt),
                Metadata: new Dictionary<string, string>
                {
                    ["relativePath"] = relativePath.Replace('\\', '/'),
                    ["workspacePath"] = workspacePath
                }));
        }

        return artifacts;
    }

    private static async Task<IReadOnlyList<string>> CollectPathsAsync(
        string workspacePath,
        string[] arguments,
        CancellationToken ct)
    {
        var result = await Cli.Wrap("git")
            .WithArguments(arguments)
            .WithWorkingDirectory(workspacePath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(" ", arguments)} failed: {result.StandardError}");

        if (string.IsNullOrWhiteSpace(result.StandardOutput))
            return Array.Empty<string>();

        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Trim().Trim('"'))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<string> ParseStatusPaths(string statusOutput)
    {
        if (string.IsNullOrWhiteSpace(statusOutput))
            return Array.Empty<string>();

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in statusOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (rawLine.Length < 4)
                continue;

            var pathSegment = rawLine[3..];
            var renameIndex = pathSegment.IndexOf(" -> ", StringComparison.Ordinal);
            if (renameIndex >= 0)
                pathSegment = pathSegment[(renameIndex + 4)..];

            var trimmed = pathSegment.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(trimmed))
                paths.Add(trimmed);
        }

        return paths.ToArray();
    }

    private static ArtifactType InferType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".md" or ".txt" or ".rst" => ArtifactType.Doc,
            ".json" or ".toml" or ".yaml" or ".yml" or ".ini" or ".cfg" or ".config" or ".csproj" or ".sln" => ArtifactType.Config,
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or ".webp" => ArtifactType.Image,
            ".wav" or ".mp3" or ".ogg" => ArtifactType.Audio,
            ".glb" or ".gltf" or ".obj" or ".fbx" => ArtifactType.Model3D,
            _ => ArtifactType.Code
        };
    }

    private static string InferFormat(string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return string.IsNullOrWhiteSpace(extension) ? "unknown" : extension;
    }
}
