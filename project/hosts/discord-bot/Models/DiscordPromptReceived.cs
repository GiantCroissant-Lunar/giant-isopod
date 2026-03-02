using Akka.Actor;

namespace GiantIsopod.DiscordBot.Models;

/// <summary>
/// A2A-style message contract for Discord prompt received events.
/// Following the A2A pattern: uses references instead of blobs for large data.
/// </summary>
public sealed class DiscordPromptReceived
{
    /// <summary>
    /// Unique correlation ID for tracking request/response pairs.
    /// </summary>
    public required string CorrelationId { get; init; }

    /// <summary>
    /// Discord user ID who sent the prompt.
    /// </summary>
    public required ulong UserId { get; init; }

    /// <summary>
    /// Discord channel ID where the prompt was sent.
    /// </summary>
    public required ulong ChannelId { get; init; }

    /// <summary>
    /// Discord guild (server) ID, if applicable.
    /// </summary>
    public ulong? GuildId { get; init; }

    /// <summary>
    /// The text content of the prompt.
    /// </summary>
    public string? TextContent { get; init; }

    /// <summary>
    /// Reference to audio content if this is a voice message.
    /// Uses A2A pattern: reference instead of blob.
    /// </summary>
    public AudioReference? AudioReference { get; init; }

    /// <summary>
    /// Timestamp when the prompt was received.
    /// </summary>
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Additional metadata about the prompt (message ID, etc.).
    /// </summary>
    public Dictionary<string, string> Metadata { get; init; } = new();

    /// <summary>
    /// Actor reference for sending responses back to the session.
    /// </summary>
    public IActorRef? ReplyTo { get; init; }
}
