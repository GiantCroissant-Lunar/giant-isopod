using GiantIsopod.Contracts.Core;

namespace GiantIsopod.Plugin.Protocol;

/// <summary>
/// Adapts Pi RPC events into AG-UI normalized event types.
/// </summary>
public static class AgUiAdapter
{
    public static AgentActivityState MapRpcEventToActivity(string rpcEventJson)
    {
        // TODO: Parse pi RPC JSON and map to AG-UI event types
        // For now, simple keyword matching
        if (rpcEventJson.Contains("\"tool_use\"")) return AgentActivityState.Typing;
        if (rpcEventJson.Contains("\"tool_result\"")) return AgentActivityState.Reading;
        if (rpcEventJson.Contains("\"thinking\"")) return AgentActivityState.Thinking;
        if (rpcEventJson.Contains("\"waiting\"")) return AgentActivityState.Waiting;
        return AgentActivityState.Idle;
    }
}
