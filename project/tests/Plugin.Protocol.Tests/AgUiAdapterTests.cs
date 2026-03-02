using GiantIsopod.Contracts.Core;
using GiantIsopod.Contracts.Protocol.AgUi;
using Xunit;

namespace GiantIsopod.Plugin.Protocol.Tests;

public class AgUiAdapterTests
{
    private readonly AgUiAdapter _adapter = new("agent-1");

    [Fact]
    public void FirstEvent_AutoStartsRun()
    {
        var events = _adapter.MapRpcEventToAgUiEvents("some text content");

        Assert.Contains(events, e => e is RunStartedEvent);
        var runStarted = events.OfType<RunStartedEvent>().Single();
        Assert.Equal("agent-1-thread", runStarted.ThreadId);
        Assert.Equal("agent-1-run-1", runStarted.RunId);
    }

    [Fact]
    public void SecondEvent_DoesNotStartAnotherRun()
    {
        _adapter.MapRpcEventToAgUiEvents("first");
        var events = _adapter.MapRpcEventToAgUiEvents("second");

        Assert.DoesNotContain(events, e => e is RunStartedEvent);
    }

    [Fact]
    public void TextContent_StartsNewMessage()
    {
        var events = _adapter.MapRpcEventToAgUiEvents("hello world");

        Assert.Contains(events, e => e is TextMessageStartEvent);
        Assert.Contains(events, e => e is TextMessageContentEvent);
        var content = events.OfType<TextMessageContentEvent>().Single();
        Assert.Equal("hello world", content.Delta);
    }

    [Fact]
    public void TextContent_SubsequentAppends()
    {
        _adapter.MapRpcEventToAgUiEvents("hello");
        var events = _adapter.MapRpcEventToAgUiEvents("world");

        // Should only have content, no new message start
        Assert.DoesNotContain(events, e => e is TextMessageStartEvent);
        Assert.Contains(events, e => e is TextMessageContentEvent);
    }

    [Fact]
    public void ToolUse_EndsActiveMessage_StartsToolCall()
    {
        _adapter.MapRpcEventToAgUiEvents("some text");
        var events = _adapter.MapRpcEventToAgUiEvents("""{"type": "tool_use", "name": "read_file"}""");

        Assert.Contains(events, e => e is TextMessageEndEvent);
        Assert.Contains(events, e => e is ToolCallStartEvent);

        var toolStart = events.OfType<ToolCallStartEvent>().Single();
        Assert.Equal("read_file", toolStart.ToolName);
        Assert.Equal("agent-1-tc-1", toolStart.ToolCallId);
    }

    [Fact]
    public void ToolResult_EndsToolCall()
    {
        _adapter.MapRpcEventToAgUiEvents("""{"type": "tool_use", "name": "bash"}""");
        var events = _adapter.MapRpcEventToAgUiEvents("""{"type": "tool_result"}""");

        Assert.Contains(events, e => e is ToolCallEndEvent);
        var toolEnd = events.OfType<ToolCallEndEvent>().Single();
        Assert.Equal("agent-1-tc-1", toolEnd.ToolCallId);
    }

    [Fact]
    public void ToolResult_WithNoActiveToolCall_ProducesNoToolEndEvent()
    {
        var events = _adapter.MapRpcEventToAgUiEvents("""{"type": "tool_result"}""");

        // RunStartedEvent is present (first event), but no ToolCallEndEvent
        Assert.DoesNotContain(events, e => e is ToolCallEndEvent);
    }

    [Fact]
    public void TextDuringToolCall_IsIgnored()
    {
        _adapter.MapRpcEventToAgUiEvents("""{"type": "tool_use", "name": "bash"}""");
        var events = _adapter.MapRpcEventToAgUiEvents("some tool output text");

        Assert.DoesNotContain(events, e => e is TextMessageStartEvent);
        Assert.DoesNotContain(events, e => e is TextMessageContentEvent);
    }

    [Fact]
    public void Exit_ClosesAllActiveState()
    {
        _adapter.MapRpcEventToAgUiEvents("some text");
        _adapter.MapRpcEventToAgUiEvents("""{"type": "tool_use", "name": "read"}""");
        var events = _adapter.MapRpcEventToAgUiEvents("""[exit] Process exited""");

        Assert.Contains(events, e => e is ToolCallEndEvent);
        Assert.Contains(events, e => e is RunFinishedEvent);

        var finished = events.OfType<RunFinishedEvent>().Single();
        Assert.Equal("agent-1-run-1", finished.RunId);
    }

    [Fact]
    public void Exit_WithOnlyTextActive_ClosesMessageAndRun()
    {
        _adapter.MapRpcEventToAgUiEvents("some text");
        var events = _adapter.MapRpcEventToAgUiEvents("""{"type": "exit"}""");

        Assert.Contains(events, e => e is TextMessageEndEvent);
        Assert.Contains(events, e => e is RunFinishedEvent);
    }

    [Fact]
    public void AfterExit_NewEventStartsFreshRun()
    {
        _adapter.MapRpcEventToAgUiEvents("text");
        _adapter.MapRpcEventToAgUiEvents("""{"type": "exit"}""");
        var events = _adapter.MapRpcEventToAgUiEvents("new run text");

        var runStarted = events.OfType<RunStartedEvent>().Single();
        Assert.Equal("agent-1-run-2", runStarted.RunId);
    }

    [Fact]
    public void ThinkingContent_IsIgnored()
    {
        var events = _adapter.MapRpcEventToAgUiEvents("""{"type": "thinking", "content": "hmm"}""");

        Assert.DoesNotContain(events, e => e is TextMessageStartEvent);
        Assert.DoesNotContain(events, e => e is TextMessageContentEvent);
    }

    [Fact]
    public void EmptyWhitespace_IsIgnored()
    {
        var events = _adapter.MapRpcEventToAgUiEvents("   ");

        // Only RunStartedEvent (first event triggers), no text events
        Assert.Single(events);
        Assert.IsType<RunStartedEvent>(events[0]);
    }

    [Fact]
    public void MultipleToolCalls_HaveIncrementingIds()
    {
        _adapter.MapRpcEventToAgUiEvents("""{"type": "tool_use", "name": "read"}""");
        _adapter.MapRpcEventToAgUiEvents("""{"type": "tool_result"}""");
        var events = _adapter.MapRpcEventToAgUiEvents("""{"type": "tool_use", "name": "write"}""");

        var toolStart = events.OfType<ToolCallStartEvent>().Single();
        Assert.Equal("agent-1-tc-2", toolStart.ToolCallId);
    }

    [Fact]
    public void ExtractToolName_FromJsonPattern()
    {
        var events = _adapter.MapRpcEventToAgUiEvents(
            """{"type": "tool_use", "name": "my_custom_tool", "id": "123"}""");

        var toolStart = events.OfType<ToolCallStartEvent>().Single();
        Assert.Equal("my_custom_tool", toolStart.ToolName);
    }

    [Fact]
    public void ExtractToolName_FallsBackToUnknown()
    {
        var events = _adapter.MapRpcEventToAgUiEvents("tool_use with no json name field");

        var toolStart = events.OfType<ToolCallStartEvent>().Single();
        Assert.Equal("unknown_tool", toolStart.ToolName);
    }
}

public class AgUiAdapterStaticMappingTests
{
    [Theory]
    [InlineData("""{"type": "tool_use"}""", AgentActivityState.Typing)]
    [InlineData("""{"type": "tool_result"}""", AgentActivityState.Reading)]
    [InlineData("""{"type": "thinking"}""", AgentActivityState.Thinking)]
    [InlineData("""{"status": "waiting"}""", AgentActivityState.Waiting)]
    [InlineData("""{"type": "text", "content": "hello"}""", AgentActivityState.Idle)]
    public void MapRpcEventToActivity_MapsCorrectState(string json, AgentActivityState expected)
    {
        var result = AgUiAdapter.MapRpcEventToActivity(json);
        Assert.Equal(expected, result);
    }
}
