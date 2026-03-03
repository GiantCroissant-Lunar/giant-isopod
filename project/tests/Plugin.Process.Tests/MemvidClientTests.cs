using GiantIsopod.Plugin.Process;
using Xunit;

namespace GiantIsopod.Plugin.Process.Tests;

public sealed class MemvidClientTests
{
    [Fact]
    public async Task PutCommitAndSearch_UseSidecarEpisodicContract()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"giant-isopod-memvid-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        var memoryPath = Path.Combine(tempRoot, "agent.mv2");
        var logPath = Path.Combine(tempRoot, "sidecar.log");
        var executablePath = CreateFakeSidecarExecutable(tempRoot);
        var previousLogPath = Environment.GetEnvironmentVariable("GIANT_ISOPOD_SIDECAR_TEST_LOG");

        try
        {
            Environment.SetEnvironmentVariable("GIANT_ISOPOD_SIDECAR_TEST_LOG", logPath);

            var client = new MemvidClient("agent-1", memoryPath, executablePath);
            await client.PutAsync(
                "hello giant isopod memory",
                "probe",
                new Dictionary<string, string> { ["taskId"] = "real-task" });
            await client.CommitAsync();

            var hits = await client.SearchAsync("hello", 5);
            var loggedCommands = await File.ReadAllLinesAsync(logPath);

            Assert.True(File.Exists(memoryPath));
            Assert.Single(hits);
            Assert.Contains("hello giant isopod memory", hits[0].Text, StringComparison.Ordinal);

            Assert.Contains(loggedCommands, line => line == $"episodic-put|hello giant isopod memory|--file|{memoryPath}|--title|probe|--tag|taskId:real-task");
            Assert.Contains(loggedCommands, line => line == $"episodic-commit|--file|{memoryPath}");
            Assert.Contains(loggedCommands, line => line == $"episodic-search|hello|--file|{memoryPath}|--top-k|5");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIANT_ISOPOD_SIDECAR_TEST_LOG", previousLogPath);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string CreateFakeSidecarExecutable(string tempRoot)
    {
        if (OperatingSystem.IsWindows())
        {
            var scriptPath = Path.Combine(tempRoot, "fake-sidecar.ps1");
            var wrapperPath = Path.Combine(tempRoot, "fake-sidecar.cmd");

            File.WriteAllText(scriptPath, """
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Args)
$logPath = $env:GIANT_ISOPOD_SIDECAR_TEST_LOG
Add-Content -Path $logPath -Value ($Args -join '|')

switch ($Args[0]) {
  'episodic-put' {
    $content = $Args[1]
    $filePath = $Args[3]
    New-Item -ItemType File -Path $filePath -Force | Out-Null
    Set-Content -Path ($filePath + '.txt') -Value $content -NoNewline
    Write-Output '{"ok":true}'
    exit 0
  }
  'episodic-search' {
    $query = $Args[1]
    $filePath = $Args[3]
    $contentPath = $filePath + '.txt'
    $content = if (Test-Path $contentPath) { Get-Content -Raw $contentPath } else { '' }
    if ($content.Contains($query)) {
      Write-Output '{"hits":[{"text":"hello giant isopod memory","title":"probe","score":1.0}]}'
    } else {
      Write-Output '{"hits":[]}'
    }
    exit 0
  }
  'episodic-commit' {
    Write-Output '{"ok":true}'
    exit 0
  }
}

exit 1
""");

            File.WriteAllText(wrapperPath, $"@echo off{Environment.NewLine}powershell -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" %*{Environment.NewLine}");
            return wrapperPath;
        }

        var executablePath = Path.Combine(tempRoot, "fake-sidecar.sh");
        File.WriteAllText(executablePath, """
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$(printf '%s|' "$@" | sed 's/|$//')" >> "${GIANT_ISOPOD_SIDECAR_TEST_LOG}"

case "${1}" in
  episodic-put)
    content="${2}"
    file="${4}"
    touch "${file}"
    printf '%s' "${content}" > "${file}.txt"
    printf '%s\n' '{"ok":true}'
    ;;
  episodic-search)
    query="${2}"
    file="${4}"
    content=""
    if [[ -f "${file}.txt" ]]; then
      content="$(cat "${file}.txt")"
    fi
    if [[ "${content}" == *"${query}"* ]]; then
      printf '%s\n' '{"hits":[{"text":"hello giant isopod memory","title":"probe","score":1.0}]}'
    else
      printf '%s\n' '{"hits":[]}'
    fi
    ;;
  episodic-commit)
    printf '%s\n' '{"ok":true}'
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
