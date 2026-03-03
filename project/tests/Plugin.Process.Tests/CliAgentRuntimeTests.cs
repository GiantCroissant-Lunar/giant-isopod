using System.Text.RegularExpressions;
using GiantIsopod.Contracts.Protocol.Runtime;
using Xunit;

namespace GiantIsopod.Plugin.Process.Tests;

public class CliAgentRuntimeTests
{
    [Fact]
    public async Task ReadEventsAsync_SupportsPromptFilePlaceholders()
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"giant-isopod-cli-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        var config = new CliRuntimeConfig
        {
            Id = "test-cli",
            DisplayName = "Test CLI",
            Executable = "cmd",
            Args = ["/c", "type", "{prompt_file_path}"]
        };

        await using var runtime = new CliAgentRuntime("agent-1", config, model: null, workDir);
        await runtime.StartAsync();
        await runtime.SendAsync("prompt-from-file");

        var output = new List<string>();
        await foreach (var line in runtime.ReadEventsAsync())
            output.Add(line);

        var transcript = string.Join(Environment.NewLine, output);
        Assert.Contains("prompt-from-file", transcript, StringComparison.Ordinal);
        Assert.Contains("promptFile=<prompt-file path=", transcript, StringComparison.Ordinal);

        var match = Regex.Match(transcript, @"promptFile=<prompt-file path=(?<path>[^>]+)>");
        Assert.True(match.Success);
        Assert.False(File.Exists(match.Groups["path"].Value));

        Directory.Delete(workDir, recursive: true);
    }
}
