using Discord;
using Discord.Audio;
using Discord.WebSocket;
using GiantIsopod.DiscordBot.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GiantIsopod.DiscordBot.Services;

/// <summary>
/// Service that manages the Discord bot connection and interactions.
/// </summary>
public class DiscordBotService : IDiscordBotService, IDisposable
{
    private readonly DiscordSocketClient _client;
    private readonly DiscordBotConfiguration _config;
    private readonly ILogger<DiscordBotService> _logger;
    private bool _isDisposed;

    public DiscordSocketClient Client => _client;

    public bool IsConnected => _client.ConnectionState == ConnectionState.Connected;

    public event Func<SocketMessage, Task>? MessageReceived;
    public event Func<SocketUser, SocketVoiceState, SocketVoiceState, Task>? VoiceStateUpdated;

    public DiscordBotService(
        IOptions<DiscordBotConfiguration> config,
        ILogger<DiscordBotService> logger)
    {
        _config = config.Value;
        _logger = logger;

        var discordConfig = new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds |
                           GatewayIntents.GuildMessages |
                           GatewayIntents.GuildVoiceStates |
                           GatewayIntents.DirectMessages |
                           GatewayIntents.MessageContent,
            AlwaysDownloadUsers = true,
            LogLevel = LogSeverity.Info
        };

        _client = new DiscordSocketClient(discordConfig);

        _client.Log += OnLogAsync;
        _client.Ready += OnReadyAsync;
        _client.MessageReceived += OnMessageReceivedAsync;
        _client.Disconnected += OnDisconnectedAsync;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var token = _config.Token;

        // Check for environment variable if config value is placeholder
        if (string.IsNullOrEmpty(token) || token.StartsWith("${"))
        {
            token = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN") ??
                   Environment.GetEnvironmentVariable("DiscordBot__Token") ??
                   throw new InvalidOperationException(
                       "Discord bot token not found. Set DISCORD_BOT_TOKEN environment variable or configure in appsettings.");
        }

        _logger.LogInformation("Starting Discord bot...");
        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Stopping Discord bot...");
        await _client.StopAsync();
        await _client.LogoutAsync();
    }

    public async Task<IMessage?> SendMessageAsync(ulong channelId, string message, CancellationToken cancellationToken = default)
    {
        try
        {
            var channel = await _client.GetChannelAsync(channelId) as IMessageChannel;
            if (channel == null)
            {
                _logger.LogWarning("Channel {ChannelId} not found", channelId);
                return null;
            }

            return await channel.SendMessageAsync(message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send message to channel {ChannelId}", channelId);
            return null;
        }
    }

    public async Task<IUserMessage> SendStreamingMessageAsync(ulong channelId, string initialContent, CancellationToken cancellationToken = default)
    {
        var channel = await _client.GetChannelAsync(channelId) as IMessageChannel;
        if (channel == null)
        {
            throw new InvalidOperationException($"Channel {channelId} not found");
        }

        // Send initial message with typing indicator placeholder
        return await channel.SendMessageAsync(initialContent);
    }

    public async Task UpdateMessageAsync(ulong channelId, ulong messageId, string newContent, CancellationToken cancellationToken = default)
    {
        try
        {
            var channel = await _client.GetChannelAsync(channelId) as IMessageChannel;
            if (channel == null) return;

            var message = await channel.GetMessageAsync(messageId);
            if (message is IUserMessage userMessage)
            {
                await userMessage.ModifyAsync(m => m.Content = newContent);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update message {MessageId} in channel {ChannelId}", messageId, channelId);
        }
    }

    public async Task<IAudioClient?> JoinVoiceChannelAsync(ulong guildId, ulong channelId, CancellationToken cancellationToken = default)
    {
        var guild = _client.GetGuild(guildId);
        if (guild == null)
        {
            _logger.LogWarning("Guild {GuildId} not found", guildId);
            return null;
        }

        var channel = guild.GetVoiceChannel(channelId);
        if (channel == null)
        {
            _logger.LogWarning("Voice channel {ChannelId} not found in guild {GuildId}", channelId, guildId);
            return null;
        }

        try
        {
            _logger.LogInformation("Joining voice channel {ChannelName} in guild {GuildName}",
                channel.Name, guild.Name);
            return await channel.ConnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to join voice channel {ChannelId}", channelId);
            return null;
        }
    }

    public async Task LeaveVoiceChannelAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        var guild = _client.GetGuild(guildId);
        if (guild == null) return;

        var audioClient = guild.AudioClient;
        if (audioClient != null)
        {
            await audioClient.StopAsync();
        }
    }

    private Task OnLogAsync(LogMessage logMessage)
    {
        var logLevel = logMessage.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            LogSeverity.Debug => LogLevel.Trace,
            _ => LogLevel.Information
        };

        _logger.Log(logLevel, logMessage.Exception, "[Discord] {Message}", logMessage.Message);
        return Task.CompletedTask;
    }

    private Task OnReadyAsync()
    {
        _logger.LogInformation("Discord bot connected as {User}", _client.CurrentUser.Username);
        _client.SetGameAsync(_config.StatusMessage);
        return Task.CompletedTask;
    }

    private Task OnMessageReceivedAsync(SocketMessage message)
    {
        // Ignore messages from the bot itself
        if (message.Author.Id == _client.CurrentUser.Id)
            return Task.CompletedTask;

        // Invoke the event
        return MessageReceived?.Invoke(message) ?? Task.CompletedTask;
    }

    private Task OnDisconnectedAsync(Exception? ex)
    {
        if (ex != null)
        {
            _logger.LogError(ex, "Discord client disconnected unexpectedly");
        }
        else
        {
            _logger.LogInformation("Discord client disconnected");
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        _client.Log -= OnLogAsync;
        _client.Ready -= OnReadyAsync;
        _client.MessageReceived -= OnMessageReceivedAsync;
        _client.Disconnected -= OnDisconnectedAsync;

        _client.Dispose();
        _isDisposed = true;

        GC.SuppressFinalize(this);
    }
}
