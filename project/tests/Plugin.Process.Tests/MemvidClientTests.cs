using GiantIsopod.Plugin.Process;
using Xunit;

namespace GiantIsopod.Plugin.Process.Tests;

public sealed class MemvidClientTests
{
    [Fact]
    public async Task PutCommitAndSearch_UseCurrentMemvidCliContract()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"giant-isopod-memvid-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        var memoryPath = Path.Combine(tempRoot, "agent.mv2");
        var logPath = Path.Combine(tempRoot, "memvid.log");
        var executablePath = CreateFakeMemvidExecutable(tempRoot);
        var previousLogPath = Environment.GetEnvironmentVariable("GIANT_ISOPOD_MEMVID_TEST_LOG");

        try
        {
            Environment.SetEnvironmentVariable("GIANT_ISOPOD_MEMVID_TEST_LOG", logPath);

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

            Assert.Contains(loggedCommands, line => line == $"create|{memoryPath}");
            Assert.Contains(loggedCommands, line => line == $"put|{memoryPath}|--title|probe|--tag|taskId=real-task");
            Assert.Contains(loggedCommands, line => line == $"verify-single-file|{memoryPath}");
            Assert.Contains(loggedCommands, line => line == $"find|--query|hello|{memoryPath}|--json|--top-k|5");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIANT_ISOPOD_MEMVID_TEST_LOG", previousLogPath);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string CreateFakeMemvidExecutable(string tempRoot)
    {
        if (OperatingSystem.IsWindows())
        {
            var scriptPath = Path.Combine(tempRoot, "fake-memvid.ps1");
            var wrapperPath = Path.Combine(tempRoot, "fake-memvid.cmd");

            File.WriteAllText(scriptPath, """
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Args)
$logPath = $env:GIANT_ISOPOD_MEMVID_TEST_LOG
Add-Content -Path $logPath -Value ($Args -join '|')

switch ($Args[0]) {
  'create' {
    New-Item -ItemType File -Path $Args[1] -Force | Out-Null
    exit 0
  }
  'put' {
    $content = [Console]::In.ReadToEnd()
    New-Item -ItemType File -Path $Args[1] -Force | Out-Null
    Set-Content -Path ($Args[1] + '.txt') -Value $content -NoNewline
    exit 0
  }
  'find' {
    $query = $Args[2]
    $contentPath = $Args[3] + '.txt'
    $content = if (Test-Path $contentPath) { Get-Content -Raw $contentPath } else { '' }
    if ($content.Contains($query)) {
      Write-Output '{"hits":[{"text":"hello giant isopod memory","title":"probe","score":1.0}]}'
    } else {
      Write-Output '{"hits":[]}'
    }
    exit 0
  }
  'verify-single-file' {
    if (Test-Path $Args[1]) { exit 0 } else { exit 1 }
  }
}

exit 1
""");

            File.WriteAllText(wrapperPath, $"@echo off{Environment.NewLine}powershell -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" %*{Environment.NewLine}");
            return wrapperPath;
        }

        var executablePath = Path.Combine(tempRoot, "fake-memvid.sh");
        File.WriteAllText(executablePath, """
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$(printf '%s|' "$@" | sed 's/|$//')" >> "${GIANT_ISOPOD_MEMVID_TEST_LOG}"

case "${1}" in
  create)
    touch "${2}"
    ;;
  put)
    content="$(cat)"
    touch "${2}"
    printf '%s' "${content}" > "${2}.txt"
    ;;
  find)
    query="${3}"
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
  verify-single-file)
    test -f "${2}"
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
