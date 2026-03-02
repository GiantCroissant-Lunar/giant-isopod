namespace GiantIsopod.DiscordBot.Models;

/// <summary>
/// Response received from the main application via Akka.Remote.
/// Supports streaming text responses.
/// </summary>
public sealed class AgentResponse
{
    /// <summary>
    /// Correlation ID matching the original request.
    /// </summary>
    public required string CorrelationId { get; init; }

    /// <summary>
    /// Response content (text chunk or complete message).
    /// </summary>
    public string? Content { get; init; }

    /// <summary>
    /// Type of response.
    /// </summary>
    public ResponseType Type { get; init; } = ResponseType.Content;

    /// <summary>
    /// Response status.
    /// </summary>
    public ResponseStatus Status { get; init; } = ResponseStatus.Success;

    /// <summary>
    /// Error message if status is Error.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Timestamp of the response.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Whether this is the final chunk of a streaming response.
    /// </summary>
    public bool IsComplete { get; init; }

    /// <summary>
    /// Chunk sequence number for ordering streamed responses.
    /// </summary>
    public int ChunkIndex { get; init; }
}

/// <summary>
/// Type of response content.
/// </summary>
public enum ResponseType
{
    /// <summary>
    /// Text content (streaming or complete).
    /// </summary>
    Content,

    /// <summary>
    /// Acknowledgment that request was received.
    /// </summary>
    Acknowledgment,

    /// <summary>
    /// Processing status update.
    /// </summary>
    StatusUpdate,

    /// <summary>
    /// Error response.
    /// </summary>
    Error
}

/// <summary>
/// Response status.
/// </summary>
public enum ResponseStatus
{
    Success,
    Partial,  // For streaming responses
    Error,
    Rejected
}
