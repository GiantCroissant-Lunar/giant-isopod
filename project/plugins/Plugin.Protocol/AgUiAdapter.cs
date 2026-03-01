using GiantIsopod.Contracts.Core;
using GiantIsopod.Contracts.Protocol.AgUi;

namespace GiantIsopod.Plugin.Protocol;

/// <summary>
/// Adapts Pi RPC events into AG-UI normalized event types.
/// Instance-based to track run/step/tool state per agent.
/// </summary>
public sealed class AgUiAdapter
{
    private readonly string _agentId;
    private string? _currentRunId;
    private string? _currentMessageId;
    private string? _currentToolCallId;
    private int _runCounter;
    private int _messageCounter;
    private int _toolCallCounter;
    private bool _runStarted;

    public AgUiAdapter(string agentId)
    {
        _agentId = agentId;
    }

    /// <summary>
    /// Maps a raw RPC event line to a list of AG-UI events.
    /// A single input line can produce multiple events (e.g., RunStarted + TextMessageStart).
    /// </summary>
    public List<object> MapRpcEventToAgUiEvents(string rpcEventLine)
    {
        var events = new List<object>();

        // Ensure run is started on first event
        if (!_runStarted)
        {
            _runCounter++;
            _currentRunId = $"{_agentId}-run-{_runCounter}";
            events.Add(new RunStartedEvent($"{_agentId}-thread", _currentRunId));
            _runStarted = true;
        }

        // Detect tool_use start
        if (rpcEventLine.Contains("\"tool_use\"") || rpcEventLine.Contains("tool_use"))
        {
            var toolName = ExtractToolName(rpcEventLine);
            _toolCallCounter++;
            _currentToolCallId = $"{_agentId}-tc-{_toolCallCounter}";

            // End any active text message
            if (_currentMessageId != null)
            {
                events.Add(new TextMessageEndEvent(_currentMessageId));
                _currentMessageId = null;
            }

            events.Add(new ToolCallStartEvent(_currentToolCallId, toolName, _currentMessageId));
        }
        // Detect tool_result
        else if (rpcEventLine.Contains("\"tool_result\"") || rpcEventLine.Contains("tool_result"))
        {
            if (_currentToolCallId != null)
            {
                events.Add(new ToolCallEndEvent(_currentToolCallId));
                _currentToolCallId = null;
            }
        }
        // Detect exit/completion
        else if (rpcEventLine.Contains("\"exit\"") || rpcEventLine.Contains("[exit]") ||
                 rpcEventLine.Contains("Process exited"))
        {
            // End any active text message
            if (_currentMessageId != null)
            {
                events.Add(new TextMessageEndEvent(_currentMessageId));
                _currentMessageId = null;
            }
            // End any active tool call
            if (_currentToolCallId != null)
            {
                events.Add(new ToolCallEndEvent(_currentToolCallId));
                _currentToolCallId = null;
            }
            if (_currentRunId != null)
            {
                events.Add(new RunFinishedEvent($"{_agentId}-thread", _currentRunId));
                _currentRunId = null;
                _runStarted = false;
            }
        }
        // Text content (not tool-related)
        else if (!string.IsNullOrWhiteSpace(rpcEventLine) &&
                 !rpcEventLine.Contains("\"thinking\"") &&
                 _currentToolCallId == null)
        {
            // Start a new message if needed
            if (_currentMessageId == null)
            {
                _messageCounter++;
                _currentMessageId = $"{_agentId}-msg-{_messageCounter}";
                events.Add(new TextMessageStartEvent(_currentMessageId));
            }
            events.Add(new TextMessageContentEvent(_currentMessageId, rpcEventLine));
        }

        return events;
    }

    /// <summary>
    /// Legacy: maps a single RPC event to an activity state.
    /// </summary>
    public static AgentActivityState MapRpcEventToActivity(string rpcEventJson)
    {
        if (rpcEventJson.Contains("\"tool_use\"")) return AgentActivityState.Typing;
        if (rpcEventJson.Contains("\"tool_result\"")) return AgentActivityState.Reading;
        if (rpcEventJson.Contains("\"thinking\"")) return AgentActivityState.Thinking;
        if (rpcEventJson.Contains("\"waiting\"")) return AgentActivityState.Waiting;
        return AgentActivityState.Idle;
    }

    private static string ExtractToolName(string line)
    {
        // Try to extract tool name from patterns like: "tool_use": {"name": "read_file", ...}
        var nameIdx = line.IndexOf("\"name\"", StringComparison.Ordinal);
        if (nameIdx >= 0)
        {
            var colonIdx = line.IndexOf(':', nameIdx + 6);
            if (colonIdx >= 0)
            {
                var quoteStart = line.IndexOf('"', colonIdx + 1);
                if (quoteStart >= 0)
                {
                    var quoteEnd = line.IndexOf('"', quoteStart + 1);
                    if (quoteEnd > quoteStart)
                        return line.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
                }
            }
        }
        return "unknown_tool";
    }
}
