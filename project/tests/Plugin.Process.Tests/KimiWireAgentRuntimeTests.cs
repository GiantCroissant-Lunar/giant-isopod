using System.Text.Json;
using GiantIsopod.Contracts.Core;
using GiantIsopod.Contracts.Protocol.Runtime;
using Xunit;

namespace GiantIsopod.Plugin.Process.Tests;

public class KimiWireAgentRuntimeTests
{
    [Fact]
    public void BuildInitializeRequest_UsesJsonRpcInitializeMethod()
    {
        var json = KimiWireAgentRuntime.BuildInitializeRequest("init-7");
        using var document = JsonDocument.Parse(json);

        Assert.Equal("2.0", document.RootElement.GetProperty("jsonrpc").GetString());
        Assert.Equal("init-7", document.RootElement.GetProperty("id").GetString());
        Assert.Equal("initialize", document.RootElement.GetProperty("method").GetString());
        Assert.Equal("1.3", document.RootElement.GetProperty("params").GetProperty("protocol_version").GetString());
    }

    [Fact]
    public async Task BuildPromptRequest_IncludesPromptAndModel()
    {
        var config = new KimiWireRuntimeConfig
        {
            Id = "kimi-wire",
            DisplayName = "Kimi Wire",
            Executable = "kimi",
            Args = ["--wire"],
            DefaultModel = new ModelSpec(ModelId: "k2", Provider: "moonshot")
        };

        var runtime = new KimiWireAgentRuntime("agent-1", config, model: null, Path.GetTempPath());
        await runtime.SendAsync("wire prompt");
        var json = runtime.BuildPromptRequest("prompt-9");
        using var document = JsonDocument.Parse(json);

        Assert.Equal("prompt", document.RootElement.GetProperty("method").GetString());
        Assert.Equal("wire prompt", document.RootElement.GetProperty("params").GetProperty("user_input").GetString());
        Assert.Equal("moonshot", document.RootElement.GetProperty("params").GetProperty("model").GetProperty("provider").GetString());
        Assert.Equal("k2", document.RootElement.GetProperty("params").GetProperty("model").GetProperty("id").GetString());
    }

    [Fact]
    public void ExtractOutputLines_ContentPart_ReturnsText()
    {
        const string json = """{"jsonrpc":"2.0","method":"event","params":{"type":"ContentPart","payload":{"text":"hello from kimi"}}}""";

        var lines = KimiWireAgentRuntime.ExtractOutputLines(json);

        Assert.Contains("hello from kimi", lines);
    }

    [Fact]
    public void TryExtractContentFragment_ContentPart_ReturnsFragment()
    {
        const string json = """{"jsonrpc":"2.0","method":"event","params":{"type":"ContentPart","payload":{"type":"text","text":"<giant-isopod-result>"}}}""";

        var extracted = KimiWireAgentRuntime.TryExtractContentFragment(json, out var fragment);

        Assert.True(extracted);
        Assert.Equal("<giant-isopod-result>", fragment);
    }

    [Fact]
    public void TryBuildResponseJson_ApprovalRequest_AutoApproves()
    {
        const string json = """{"jsonrpc":"2.0","id":"req-5","method":"request","params":{"type":"ApprovalRequest","payload":{"id":"approval-1","options":[{"id":"approve","label":"Approve"}]}}}""";

        var handled = KimiWireAgentRuntime.TryBuildResponseJson(json, out var response);

        Assert.True(handled);
        Assert.NotNull(response);
        Assert.Contains(@"""request_id"":""approval-1""", response, StringComparison.Ordinal);
        Assert.Contains(@"""response"":""approve""", response, StringComparison.Ordinal);
    }

    [Fact]
    public void TryBuildResponseJson_UnsupportedToolRequest_ReturnsError()
    {
        const string json = """{"jsonrpc":"2.0","id":"req-11","method":"request","params":{"type":"ToolCallRequest","payload":{"id":"tool-7"}}}""";

        var handled = KimiWireAgentRuntime.TryBuildResponseJson(json, out var response);

        Assert.True(handled);
        Assert.NotNull(response);
        Assert.Contains(@"""tool_call_id"":""tool-7""", response, StringComparison.Ordinal);
        Assert.Contains(@"""is_error"":true", response, StringComparison.Ordinal);
    }
}
