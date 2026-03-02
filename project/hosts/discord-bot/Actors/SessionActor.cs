using Akka.Actor;
using Akka.Event;
using GiantIsopod.DiscordBot.Configuration;
using GiantIsopod.DiscordBot.Models;
using GiantIsopod.DiscordBot.Services;

namespace GiantIsopod.DiscordBot.Actors;

/// <summary>
/// Per-user session actor that manages state and communication with the main app via Akka.Remote.
/// Handles correlation IDs, request/response tracking, and streaming responses.
/// </summary>
public class SessionActor : ReceiveActor, ILogReceive
{
    private readonly ulong _userId;
    private readonly ulong _channelId;
    private readonly ulong? _guildId;
    private readonly IDiscordBotService _discordService;
    private readonly AkkaConfiguration _akkaConfig;
    private readonly ILoggingAdapter _log;

    // Track ongoing requests by correlation ID
    private readonly Dictionary<string, RequestState> _activeRequests = new();

    // Actor selection to the main app gateway
    private ActorSelection? _mainAppGateway;

    // Streaming message tracking
    private readonly Dictionary<string, StreamingMessageState> _streamingMessages = new();

    public SessionActor(
        ulong userId,
        ulong channelId,
        ulong? guildId,
        IDiscordBotService discordService,
        AkkaConfiguration akkaConfig)
    {
        _userId = userId;
        _channelId = channelId;
        _guildId = guildId;
        _discordService = discordService;
        _akkaConfig = akkaConfig;
        _log = Context.GetLogger();

        Receive<DiscordPromptReceived>(OnDiscordPromptReceived);
        Receive<AgentResponse>(OnAgentResponse);
        Receive<RequestTimeout>(OnRequestTimeout);
        Receive<SendStreamingChunk>(OnSendStreamingChunk);
    }

    protected override void PreStart()
    {
        _log.Info(
            "SessionActor started for user {0} in channel {1}",
            _userId, _channelId);

        // Initialize the remote actor selection
        _mainAppGateway = Context.ActorSelection(_akkaConfig.TargetActor.ActorSelectionPath);
    }

    protected override void PostStop()
    {
        _log.Info(
            "SessionActor stopped for user {0} in channel {1}",
            _userId, _channelId);

        // Notify parent that this session is terminated
        Context.Parent.Tell(new SessionTerminated { SessionRef = Self });

        base.PostStop();
    }

    private async void OnDiscordPromptReceived(DiscordPromptReceived prompt)
    {
        try
        {
            _log.Info(
                "Processing prompt {0} from user {1}",
                prompt.CorrelationId, prompt.UserId);

            // Create the agent request
            var request = new AgentRequest
            {
                CorrelationId = prompt.CorrelationId,
                PromptText = prompt.TextContent,
                AudioReference = prompt.AudioReference,
                UserContext = new UserContext
                {
                    UserId = _userId,
                    Username = "unknown", // Could be enriched from Discord context
                    ChannelId = _channelId,
                    GuildId = _guildId
                },
                Type = prompt.AudioReference != null
                    ? RequestType.Mixed
                    : RequestType.Text
            };

            // Track the request
            var requestState = new RequestState
            {
                CorrelationId = prompt.CorrelationId,
                StartedAt = DateTimeOffset.UtcNow,
                DiscordMessageId = null // Will be set when we send initial message
            };
            _activeRequests[prompt.CorrelationId] = requestState;

            // Send acknowledgment to Discord
            var ackMessage = prompt.AudioReference != null
                ? "🎤 Processing your voice message..."
                : "💭 Thinking...";

            var discordMessage = await _discordService.SendStreamingMessageAsync(
                _channelId, ackMessage);

            if (discordMessage != null)
            {
                requestState.DiscordMessageId = discordMessage.Id;
                _streamingMessages[prompt.CorrelationId] = new StreamingMessageState
                {
                    DiscordMessageId = discordMessage.Id,
                    CurrentContent = ackMessage,
                    LastUpdate = DateTimeOffset.UtcNow
                };
            }

            // Send request to main app via Akka.Remote
            _mainAppGateway?.Tell(request, Self);

            // Set timeout for this request
            Context.System.Scheduler.ScheduleTellOnce(
                TimeSpan.FromMinutes(5),
                Self,
                new RequestTimeout { CorrelationId = prompt.CorrelationId },
                Self);
        }
        catch (Exception ex)
        {
            _log.Error(
                ex, "Error processing prompt {0}", prompt.CorrelationId);

            await _discordService.SendMessageAsync(
                _channelId,
                "❌ Sorry, I encountered an error processing your request.");
        }
    }

    private async void OnAgentResponse(AgentResponse response)
    {
        try
        {
            if (!_activeRequests.TryGetValue(response.CorrelationId, out var requestState))
            {
                _log.Warning(
                    "Received response for unknown correlation ID: {0}",
                    response.CorrelationId);
                return;
            }

            switch (response.Type)
            {
                case ResponseType.Acknowledgment:
                    _log.Debug(
                        "Request {0} acknowledged by main app",
                        response.CorrelationId);
                    break;

                case ResponseType.StatusUpdate:
                    // Update the Discord message with status
                    if (requestState.DiscordMessageId.HasValue)
                    {
                        await _discordService.UpdateMessageAsync(
                            _channelId,
                            requestState.DiscordMessageId.Value,
                            response.Content ?? "⏳ Processing...");
                    }
                    break;

                case ResponseType.Content:
                    await HandleStreamingContent(response, requestState);
                    break;

                case ResponseType.Error:
                    await HandleErrorResponse(response, requestState);
                    break;
            }

            // If complete, clean up
            if (response.IsComplete || response.Status == ResponseStatus.Error)
            {
                _activeRequests.Remove(response.CorrelationId);
                _streamingMessages.Remove(response.CorrelationId);
            }
        }
        catch (Exception ex)
        {
            _log.Error(
                ex, "Error handling agent response for {0}",
                response.CorrelationId);
        }
    }

    private async Task HandleStreamingContent(AgentResponse response, RequestState requestState)
    {
        if (!requestState.DiscordMessageId.HasValue) return;

        var messageId = requestState.DiscordMessageId.Value;

        if (_streamingMessages.TryGetValue(response.CorrelationId, out var streamState))
        {
            // Append new content
            streamState.CurrentContent = response.Content ?? streamState.CurrentContent;
            streamState.LastUpdate = DateTimeOffset.UtcNow;

            // Update Discord message (with rate limiting considerations)
            // In production, you might batch updates or use webhook editing
            await _discordService.UpdateMessageAsync(
                _channelId,
                messageId,
                streamState.CurrentContent);

            // If complete, add a reaction or final indicator
            if (response.IsComplete)
            {
                _log.Info(
                    "Streaming response complete for {0}",
                    response.CorrelationId);
            }
        }
    }

    private async Task HandleErrorResponse(AgentResponse response, RequestState requestState)
    {
        var errorMessage = $"❌ Error: {response.ErrorMessage ?? "Unknown error occurred"}";

        if (requestState.DiscordMessageId.HasValue)
        {
            await _discordService.UpdateMessageAsync(
                _channelId,
                requestState.DiscordMessageId.Value,
                errorMessage);
        }
        else
        {
            await _discordService.SendMessageAsync(_channelId, errorMessage);
        }
    }

    private async void OnRequestTimeout(RequestTimeout timeout)
    {
        if (_activeRequests.Remove(timeout.CorrelationId, out var requestState))
        {
            _log.Warning(
                "Request {0} timed out",
                timeout.CorrelationId);

            var timeoutMessage = "⏱️ Request timed out. Please try again.";

            if (requestState.DiscordMessageId.HasValue)
            {
                await _discordService.UpdateMessageAsync(
                    _channelId,
                    requestState.DiscordMessageId.Value,
                    timeoutMessage);
            }
            else
            {
                await _discordService.SendMessageAsync(_channelId, timeoutMessage);
            }

            _streamingMessages.Remove(timeout.CorrelationId);
        }
    }

    private void OnSendStreamingChunk(SendStreamingChunk chunk)
    {
        // Internal message for scheduling streaming updates
        if (_streamingMessages.TryGetValue(chunk.CorrelationId, out var streamState))
        {
            streamState.CurrentContent = chunk.Content;
        }
    }
}

/// <summary>
/// Tracks the state of an active request.
/// </summary>
internal class RequestState
{
    public required string CorrelationId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public ulong? DiscordMessageId { get; set; }
}

/// <summary>
/// Tracks the state of a streaming Discord message.
/// </summary>
internal class StreamingMessageState
{
    public required ulong DiscordMessageId { get; init; }
    public required string CurrentContent { get; set; }
    public required DateTimeOffset LastUpdate { get; set; }
}

// Internal messages for SessionActor
internal sealed class RequestTimeout
{
    public required string CorrelationId { get; init; }
}

internal sealed class SendStreamingChunk
{
    public required string CorrelationId { get; init; }
    public required string Content { get; init; }
}
