namespace GiantIsopod.DiscordBot.Models;

/// <summary>
/// Request sent from Discord bot to the main application via Akka.Remote.
/// </summary>
public sealed class AgentRequest
{
    /// <summary>
    /// Unique correlation ID for request/response tracking.
    /// </summary>
    public required string CorrelationId { get; init; }

    /// <summary>
    /// The prompt text to send to the agent.
    /// </summary>
    public string? PromptText { get; init; }

    /// <summary>
    /// Reference to audio content if the prompt includes voice.
    /// </summary>
    public AudioReference? AudioReference { get; init; }

    /// <summary>
    /// User context information.
    /// </summary>
    public UserContext UserContext { get; init; } = new();

    /// <summary>
    /// Request timestamp.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Request type classification.
    /// </summary>
    public RequestType Type { get; init; } = RequestType.Text;
}

/// <summary>
/// User context for the request.
/// </summary>
public sealed class UserContext
{
    public ulong UserId { get; init; }

    public string Username { get; init; } = string.Empty;

    public ulong ChannelId { get; init; }

    public ulong? GuildId { get; init; }
}

/// <summary>
/// Type of request being made.
/// </summary>
public enum RequestType
{
    Text,
    Voice,
    Mixed
}
