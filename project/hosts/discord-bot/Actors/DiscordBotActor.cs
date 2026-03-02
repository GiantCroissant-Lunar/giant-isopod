using Akka.Actor;
using Discord.WebSocket;
using GiantIsopod.DiscordBot.Configuration;
using GiantIsopod.DiscordBot.Models;
using GiantIsopod.DiscordBot.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GiantIsopod.DiscordBot.Actors;

/// <summary>
/// Main bot actor that connects to Discord and manages session actors.
/// Receives Discord events and dispatches to appropriate session actors.
/// </summary>
public class DiscordBotActor : ReceiveActor
{
    private readonly ILogger<DiscordBotActor> _logger;
    private readonly IDiscordBotService _discordService;
    private readonly AkkaConfiguration _akkaConfig;
    private readonly Dictionary<string, IActorRef> _sessions = new();

    public DiscordBotActor(
        IDiscordBotService discordService,
        IOptions<AkkaConfiguration> akkaConfig,
        ILogger<DiscordBotActor> logger)
    {
        _discordService = discordService;
        _akkaConfig = akkaConfig.Value;
        _logger = logger;

        Receive<StartBot>(OnStartBot);
        Receive<StopBot>(OnStopBot);
        Receive<DiscordMessageReceived>(OnDiscordMessageReceived);
        Receive<DiscordVoiceReceived>(OnDiscordVoiceReceived);
        Receive<SessionTerminated>(OnSessionTerminated);
        Receive<Models.AgentResponse>(OnAgentResponse);
    }

    protected override void PreStart()
    {
        _logger.LogInformation("DiscordBotActor starting...");
        _discordService.MessageReceived += OnDiscordMessageAsync;
    }

    protected override void PostStop()
    {
        _logger.LogInformation("DiscordBotActor stopping...");
        _discordService.MessageReceived -= OnDiscordMessageAsync;
        base.PostStop();
    }

    private void OnStartBot(StartBot msg)
    {
        _logger.LogInformation("Starting Discord bot from actor...");

        // Fire and forget the start task
        _discordService.StartAsync()
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    return new BotStartFailed { Reason = t.Exception?.InnerException?.Message ?? "Unknown error" };
                }
                return (object)new BotStarted();
            }, TaskScheduler.Default)
            .PipeTo(Self);
    }

    private void OnStopBot(StopBot msg)
    {
        _logger.LogInformation("Stopping Discord bot from actor...");

        // Stop all sessions first
        foreach (var session in _sessions.Values)
        {
            session.Tell(PoisonPill.Instance);
        }
        _sessions.Clear();

        _discordService.StopAsync()
            .ContinueWith(t => new BotStopped(), TaskScheduler.Default)
            .PipeTo(Self);
    }

    private void OnDiscordMessageReceived(DiscordMessageReceived msg)
    {
        var sessionKey = GetSessionKey(msg.UserId, msg.ChannelId);

        if (!_sessions.TryGetValue(sessionKey, out var sessionActor))
        {
            _logger.LogInformation("Creating new session for user {UserId} in channel {ChannelId}",
                msg.UserId, msg.ChannelId);

            // Create a new session actor
            var sessionProps = Props.Create<SessionActor>(
                msg.UserId,
                msg.ChannelId,
                msg.GuildId,
                _discordService,
                _akkaConfig);

            sessionActor = Context.ActorOf(sessionProps, $"session-{sessionKey}");
            _sessions[sessionKey] = sessionActor;

            // Watch the session actor
            Context.Watch(sessionActor);
        }

        // Forward the message to the session actor
        var prompt = new DiscordPromptReceived
        {
            CorrelationId = Guid.NewGuid().ToString("N"),
            UserId = msg.UserId,
            ChannelId = msg.ChannelId,
            GuildId = msg.GuildId,
            TextContent = msg.Content,
            ReplyTo = Self
        };

        sessionActor.Tell(prompt);
    }

    private void OnDiscordVoiceReceived(DiscordVoiceReceived msg)
    {
        var sessionKey = GetSessionKey(msg.UserId, msg.ChannelId);

        if (!_sessions.TryGetValue(sessionKey, out var sessionActor))
        {
            _logger.LogInformation("Creating new session for voice message from user {UserId}", msg.UserId);

            var sessionProps = Props.Create<SessionActor>(
                msg.UserId,
                msg.ChannelId,
                msg.GuildId,
                _discordService,
                _akkaConfig);

            sessionActor = Context.ActorOf(sessionProps, $"session-{sessionKey}");
            _sessions[sessionKey] = sessionActor;
            Context.Watch(sessionActor);
        }

        // Forward to session with audio reference
        var prompt = new DiscordPromptReceived
        {
            CorrelationId = Guid.NewGuid().ToString("N"),
            UserId = msg.UserId,
            ChannelId = msg.ChannelId,
            GuildId = msg.GuildId,
            AudioReference = msg.AudioReference,
            ReplyTo = Self
        };

        sessionActor.Tell(prompt);
    }

    private void OnSessionTerminated(SessionTerminated msg)
    {
        var keyToRemove = _sessions
            .Where(kvp => kvp.Value.Equals(msg.SessionRef))
            .Select(kvp => kvp.Key)
            .FirstOrDefault();

        if (keyToRemove != null)
        {
            _sessions.Remove(keyToRemove);
            _logger.LogInformation("Session {SessionKey} terminated and removed", keyToRemove);
        }
    }

    private void OnAgentResponse(Models.AgentResponse response)
    {
        // Responses are handled by session actors, but if we get one directly,
        // it might be a system message or error
        _logger.LogDebug("Received actor response for correlation {CorrelationId}: {Status}",
            response.CorrelationId, response.Status);
    }

    private async Task OnDiscordMessageAsync(SocketMessage message)
    {
        // Ignore bot messages and system messages
        if (message.Author.IsBot || message.Author.IsWebhook)
            return;

        // Handle text messages
        if (message is SocketUserMessage userMessage)
        {
            var msg = new DiscordMessageReceived
            {
                UserId = message.Author.Id,
                ChannelId = message.Channel.Id,
                GuildId = (message.Channel as SocketGuildChannel)?.Guild.Id,
                Content = message.Content,
                MessageId = message.Id,
                Timestamp = DateTimeOffset.UtcNow
            };

            Self.Tell(msg);
        }

        await Task.CompletedTask;
    }

    private static string GetSessionKey(ulong userId, ulong channelId)
    {
        return $"{userId}-{channelId}";
    }
}

// Messages for DiscordBotActor
public sealed class StartBot;
public sealed class StopBot;
public sealed class BotStarted;
public sealed class BotStartFailed
{
    public required string Reason { get; init; }
}
public sealed class BotStopped;

public sealed class DiscordMessageReceived
{
    public required ulong UserId { get; init; }
    public required ulong ChannelId { get; init; }
    public ulong? GuildId { get; init; }
    public required string Content { get; init; }
    public required ulong MessageId { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

public sealed class DiscordVoiceReceived
{
    public required ulong UserId { get; init; }
    public required ulong ChannelId { get; init; }
    public ulong? GuildId { get; init; }
    public required AudioReference AudioReference { get; init; }
}

public sealed class SessionTerminated
{
    public required IActorRef SessionRef { get; init; }
}
