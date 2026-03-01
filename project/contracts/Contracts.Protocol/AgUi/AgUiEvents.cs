namespace GiantIsopod.Contracts.Protocol.AgUi;

// ── AG-UI Protocol Events ──
// Based on the AG-UI specification: Lifecycle, TextMessage, ToolCall, State, Special

// -- Lifecycle events --

public record RunStartedEvent(string ThreadId, string RunId);
public record RunFinishedEvent(string ThreadId, string RunId);
public record RunErrorEvent(string ThreadId, string RunId, string Message, string? Code = null);
public record StepStartedEvent(string ThreadId, string RunId, string StepName);
public record StepFinishedEvent(string ThreadId, string RunId, string StepName);

// -- TextMessage events --

public record TextMessageStartEvent(string MessageId, string Role = "assistant");
public record TextMessageContentEvent(string MessageId, string Delta);
public record TextMessageEndEvent(string MessageId);

// -- ToolCall events --

public record ToolCallStartEvent(string ToolCallId, string ToolName, string? ParentMessageId = null);
public record ToolCallArgsEvent(string ToolCallId, string Delta);
public record ToolCallEndEvent(string ToolCallId);
public record ToolCallResultEvent(string ToolCallId, string Result);

// -- State events --

public record StateSnapshotEvent(IReadOnlyDictionary<string, object?> State);
public record StateDeltaEvent(IReadOnlyList<JsonPatchOp> Patches);
public record MessagesSnapshotEvent(IReadOnlyList<AgUiMessage> Messages);

// -- Special events --

public record RawEvent(string EventType, string Payload);
public record CustomEvent(string Name, IReadOnlyDictionary<string, object?>? Data = null);

// -- Supporting types --

public record JsonPatchOp(string Op, string Path, object? Value = null);
public record AgUiMessage(string Role, string Content, string? MessageId = null);
