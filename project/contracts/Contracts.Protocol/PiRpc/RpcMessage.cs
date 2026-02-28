using System.Text.Json.Serialization;

namespace GiantIsopod.Contracts.Protocol.PiRpc;

/// <summary>
/// Pi RPC protocol messages. Will be replaced by quicktype-generated types
/// once the full pi RPC schema is captured.
/// </summary>
public record RpcMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("data")]
    public object? Data { get; init; }
}

public record RpcPromptRequest
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "prompt";

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

public record RpcToolEvent
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("tool_name")]
    public string? ToolName { get; init; }

    [JsonPropertyName("tool_id")]
    public string? ToolId { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }
}
