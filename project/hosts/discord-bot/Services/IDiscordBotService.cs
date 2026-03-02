using Discord;
using Discord.Audio;
using Discord.WebSocket;

namespace GiantIsopod.DiscordBot.Services;

/// <summary>
/// Interface for the Discord bot service that manages the Discord client connection.
/// </summary>
public interface IDiscordBotService
{
    /// <summary>
    /// The Discord socket client.
    /// </summary>
    DiscordSocketClient Client { get; }

    /// <summary>
    /// Whether the bot is currently connected.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Event raised when a message is received.
    /// </summary>
    event Func<SocketMessage, Task> MessageReceived;

    /// <summary>
    /// Starts the bot and connects to Discord.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the bot and disconnects from Discord.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a text message to a channel.
    /// </summary>
    Task<IMessage?> SendMessageAsync(ulong channelId, string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a streaming text message that can be updated.
    /// </summary>
    Task<IUserMessage> SendStreamingMessageAsync(ulong channelId, string initialContent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing message.
    /// </summary>
    Task UpdateMessageAsync(ulong channelId, ulong messageId, string newContent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Joins a voice channel.
    /// </summary>
    Task<IAudioClient?> JoinVoiceChannelAsync(ulong guildId, ulong channelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Leaves a voice channel.
    /// </summary>
    Task LeaveVoiceChannelAsync(ulong guildId, CancellationToken cancellationToken = default);
}
