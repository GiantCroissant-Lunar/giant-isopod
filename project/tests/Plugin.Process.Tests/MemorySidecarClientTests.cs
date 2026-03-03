using GiantIsopod.Plugin.Process;
using Xunit;

namespace GiantIsopod.Plugin.Process.Tests;

public sealed class MemorySidecarClientTests
{
    [Fact]
    public async Task StoreAndQuery_ResolveExecutableFromPath()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"giant-isopod-sidecar-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        var fakeBin = Path.Combine(tempRoot, "bin");
        Directory.CreateDirectory(fakeBin);
        var dataDir = Path.Combine(tempRoot, "data");
        Directory.CreateDirectory(dataDir);
        var logPath = Path.Combine(tempRoot, "sidecar.log");
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        var previousLog = Environment.GetEnvironmentVariable("GIANT_ISOPOD_SIDECAR_TEST_LOG");
        CreateFakeSidecarExecutable(fakeBin);

        try
        {
            Environment.SetEnvironmentVariable("PATH", $"{fakeBin}{Path.PathSeparator}{previousPath}");
            Environment.SetEnvironmentVariable("GIANT_ISOPOD_SIDECAR_TEST_LOG", logPath);

            var client = new MemorySidecarClient(dataDir, "memory-sidecar");
            var id = await client.StoreKnowledgeAsync(
                "agent-1",
                "Avoid circular dependency in payment module",
                "pitfall",
                new Dictionary<string, string> { ["task"] = "payments" });
            var results = await client.SearchKnowledgeAsync("agent-1", "payment dependency", topK: 5);
            var loggedCommands = await File.ReadAllLinesAsync(logPath);

            Assert.Equal(1, id);
            Assert.Single(results);
            Assert.Equal("pitfall", results[0].Category);
            Assert.Contains("Avoid circular dependency", results[0].Content, StringComparison.Ordinal);

            var expectedDbFile = "agent-1.sqlite";
            var storeLine = Assert.Single(loggedCommands, line => line.StartsWith("store|", StringComparison.Ordinal));
            Assert.Contains("Avoid circular dependency in payment module", storeLine, StringComparison.Ordinal);
            Assert.Contains("--agent|agent-1", storeLine, StringComparison.Ordinal);
            Assert.Contains("--category|pitfall", storeLine, StringComparison.Ordinal);
            Assert.Contains("--tag|task:payments", storeLine, StringComparison.Ordinal);
            Assert.Contains(expectedDbFile, storeLine, StringComparison.Ordinal);

            var queryLine = Assert.Single(loggedCommands, line => line.StartsWith("query|", StringComparison.Ordinal));
            Assert.Contains("payment dependency", queryLine, StringComparison.Ordinal);
            Assert.Contains("--agent|agent-1", queryLine, StringComparison.Ordinal);
            Assert.Contains("--top-k|5", queryLine, StringComparison.Ordinal);
            Assert.Contains("--json-output", queryLine, StringComparison.Ordinal);
            Assert.Contains(expectedDbFile, queryLine, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Environment.SetEnvironmentVariable("GIANT_ISOPOD_SIDECAR_TEST_LOG", previousLog);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string CreateFakeSidecarExecutable(string fakeBin)
    {
        if (OperatingSystem.IsWindows())
        {
            var scriptPath = Path.Combine(fakeBin, "memory-sidecar.ps1");
            var wrapperPath = Path.Combine(fakeBin, "memory-sidecar.cmd");

            File.WriteAllText(scriptPath, """
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Args)
$logPath = $env:GIANT_ISOPOD_SIDECAR_TEST_LOG
Add-Content -Path $logPath -Value ($Args -join '|')

switch ($Args[0]) {
  'store' {
    Write-Output '{"id":1,"agent":"agent-1","category":"pitfall"}'
    exit 0
  }
  'query' {
    Write-Output '[{"content":"Avoid circular dependency in payment module","category":"pitfall","tags":{"task":"payments"},"stored_at":"2026-03-03T00:00:00+00:00","relevance":0.9}]'
    exit 0
  }
}

exit 1
""");

            File.WriteAllText(wrapperPath, $"@echo off{Environment.NewLine}powershell -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" %*{Environment.NewLine}");
            return wrapperPath;
        }

        var executablePath = Path.Combine(fakeBin, "memory-sidecar");
        File.WriteAllText(executablePath, """
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$(printf '%s|' "$@" | sed 's/|$//')" >> "${GIANT_ISOPOD_SIDECAR_TEST_LOG}"

case "${1}" in
  store)
    printf '%s\n' '{"id":1,"agent":"agent-1","category":"pitfall"}'
    ;;
  query)
    printf '%s\n' '[{"content":"Avoid circular dependency in payment module","category":"pitfall","tags":{"task":"payments"},"stored_at":"2026-03-03T00:00:00+00:00","relevance":0.9}]'
    ;;
  *)
    exit 1
    ;;
esac
""");

        File.SetUnixFileMode(executablePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return executablePath;
    }
}
