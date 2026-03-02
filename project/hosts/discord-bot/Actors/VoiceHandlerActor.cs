using Akka.Actor;
using Discord;
using Discord.Audio;
using GiantIsopod.DiscordBot.Configuration;
using GiantIsopod.DiscordBot.Models;
using GiantIsopod.DiscordBot.Services;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using static Akka.Actor.PipeToSupport;

namespace GiantIsopod.DiscordBot.Actors;

/// <summary>
/// Handles voice channel connections, audio recording, and upload to storage.
/// One VoiceHandlerActor per guild to manage voice connections.
/// </summary>
public class VoiceHandlerActor : ReceiveActor
{
    private readonly ulong _guildId;
    private readonly IDiscordBotService _discordService;
    private readonly IStorageService _storageService;
    private readonly VoiceConfiguration _voiceConfig;
    private readonly ILogger<VoiceHandlerActor> _logger;

    // Track active voice connections
    private readonly Dictionary<ulong, VoiceConnectionState> _connections = new();

    // Audio buffer for each user being recorded
    private readonly ConcurrentDictionary<ulong, AudioBuffer> _audioBuffers = new();

    public VoiceHandlerActor(
        ulong guildId,
        IDiscordBotService discordService,
        IStorageService storageService,
        VoiceConfiguration voiceConfig,
        ILogger<VoiceHandlerActor> logger)
    {
        _guildId = guildId;
        _discordService = discordService;
        _storageService = storageService;
        _voiceConfig = voiceConfig;
        _logger = logger;

        Receive<StartVoiceRecording>(OnStartVoiceRecording);
        Receive<StopVoiceRecording>(OnStopVoiceRecording);
        Receive<ProcessVoiceData>(OnProcessVoiceData);
        Receive<UploadAudioComplete>(OnUploadAudioComplete);
    }

    protected override void PreStart()
    {
        _logger.LogInformation("VoiceHandlerActor started for guild {GuildId}", _guildId);
    }

    protected override void PostStop()
    {
        // Clean up any active connections
        foreach (var connection in _connections.Values)
        {
            connection.AudioClient?.StopAsync().Wait(TimeSpan.FromSeconds(5));
        }
        _connections.Clear();
        _audioBuffers.Clear();

        base.PostStop();
    }

    private void OnStartVoiceRecording(StartVoiceRecording msg)
    {
        if (_connections.ContainsKey(msg.ChannelId))
        {
            _logger.LogWarning("Already recording in channel {ChannelId}", msg.ChannelId);
            return;
        }

        _logger.LogInformation(
            "Joining voice channel {ChannelId} in guild {GuildId}",
            msg.ChannelId, _guildId);

        _discordService.JoinVoiceChannelAsync(_guildId, msg.ChannelId)
            .PipeTo(Self, success: audioClient =>
            {
                if (audioClient == null)
                {
                    _logger.LogError("Failed to join voice channel {ChannelId}", msg.ChannelId);
                    return new VoiceConnectionFailed { ChannelId = msg.ChannelId, UserId = msg.UserId };
                }

                var connectionState = new VoiceConnectionState
                {
                    ChannelId = msg.ChannelId,
                    AudioClient = audioClient,
                    StartedAt = DateTimeOffset.UtcNow,
                    RecordingUserId = msg.UserId
                };

                _connections[msg.ChannelId] = connectionState;

                // Start recording from the audio stream
                _ = Task.Run(async () => await RecordAudioAsync(msg.ChannelId, audioClient, msg.UserId));

                // Set max duration timeout
                var maxDuration = msg.MaxDurationSeconds ?? _voiceConfig.MaxRecordingDurationSeconds;
                Context.System.Scheduler.ScheduleTellOnce(
                    TimeSpan.FromSeconds(maxDuration),
                    Self,
                    new StopVoiceRecording { GuildId = _guildId, ChannelId = msg.ChannelId },
                    Self);

                _logger.LogInformation(
                    "Started recording in channel {ChannelId} for user {UserId}",
                    msg.ChannelId, msg.UserId);

                return new VoiceRecordingStarted { ChannelId = msg.ChannelId, UserId = msg.UserId };
            }, failure: ex =>
            {
                _logger.LogError(ex, "Error starting voice recording in channel {ChannelId}", msg.ChannelId);
                return new VoiceConnectionFailed { ChannelId = msg.ChannelId, UserId = msg.UserId };
            });
    }

    private void OnStopVoiceRecording(StopVoiceRecording msg)
    {
        if (!_connections.TryGetValue(msg.ChannelId, out var connectionState))
        {
            _logger.LogWarning("Not recording in channel {ChannelId}", msg.ChannelId);
            return;
        }

        _logger.LogInformation(
            "Stopping recording in channel {ChannelId}",
            msg.ChannelId);

        // Stop the audio client
        _discordService.LeaveVoiceChannelAsync(_guildId)
            .PipeTo(Self, success: () =>
            {
                // Process any remaining audio data
                if (_audioBuffers.TryRemove(connectionState.RecordingUserId, out var buffer))
                {
                    _ = FinalizeRecordingAsync(buffer, connectionState);
                }

                _connections.Remove(msg.ChannelId);

                return new VoiceRecordingStopped { ChannelId = msg.ChannelId };
            }, failure: ex =>
            {
                _logger.LogError(ex, "Error stopping voice recording in channel {ChannelId}", msg.ChannelId);
                return new VoiceRecordingFailed { ChannelId = msg.ChannelId, Error = ex.Message };
            });
    }

    private async Task RecordAudioAsync(ulong channelId, IAudioClient audioClient, ulong userId)
    {
        try
        {
            // Initialize audio buffer for this user
            var audioBuffer = new AudioBuffer
            {
                UserId = userId,
                ChannelId = channelId,
                Data = new MemoryStream(),
                StartedAt = DateTimeOffset.UtcNow
            };
            _audioBuffers[userId] = audioBuffer;

            // Note: Discord.Net's audio receiving requires opus decoding
            // This is a simplified implementation - actual audio capture would use
            // Discord.Audio streams and opus decoder

            // For now, we'll simulate the recording flow
            // In a real implementation, you'd subscribe to audioClient.StreamCreated
            // and process the incoming audio packets

            _logger.LogInformation("Audio recording stream started for user {UserId}", userId);

            // Keep the connection alive until stopped
            while (_connections.ContainsKey(channelId))
            {
                await Task.Delay(100);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in audio recording stream");
        }
    }

    private async Task FinalizeRecordingAsync(AudioBuffer buffer, VoiceConnectionState connectionState)
    {
        try
        {
            var duration = DateTimeOffset.UtcNow - buffer.StartedAt;
            var recordingId = $"{buffer.UserId}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";

            _logger.LogInformation(
                "Finalizing recording {RecordingId} for user {UserId}, duration: {Duration}",
                recordingId, buffer.UserId, duration);

            // Convert to proper audio format (WAV)
            // In a real implementation, you'd convert from opus to WAV here
            var audioStream = CreateWavStream(buffer.Data);

            // Upload to storage
            var audioReference = await _storageService.UploadAudioAsync(
                audioStream,
                recordingId,
                _voiceConfig.RecordingFormat);

            // Create the result with updated duration
            var result = new VoiceRecordingResult
            {
                RecordingId = recordingId,
                AudioReference = audioReference,
                UserId = buffer.UserId,
                DurationSeconds = duration.TotalSeconds
            };

            // Send result back to parent (DiscordBotActor)
            Context.Parent.Tell(new DiscordVoiceReceived
            {
                UserId = buffer.UserId,
                ChannelId = buffer.ChannelId,
                AudioReference = audioReference
            });

            _logger.LogInformation(
                "Audio upload complete for recording {RecordingId}: {Url}",
                recordingId, audioReference.DownloadUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finalizing recording for user {UserId}", buffer.UserId);
        }
    }

    private void OnProcessVoiceData(ProcessVoiceData msg)
    {
        // Process incoming voice data packets
        // This would be called from the audio stream handler
        if (_audioBuffers.TryGetValue(msg.UserId, out var buffer))
        {
            buffer.Data.Write(msg.Data, 0, msg.Data.Length);
        }
    }

    private void OnUploadAudioComplete(UploadAudioComplete msg)
    {
        _logger.LogInformation(
            "Audio upload completed for recording {RecordingId}",
            msg.RecordingId);
    }

    /// <summary>
    /// Creates a properly formatted WAV stream from raw audio data.
    /// </summary>
    private static MemoryStream CreateWavStream(Stream rawData)
    {
        // This is a simplified implementation
        // Real implementation would need proper opus decoding and WAV header creation

        var wavStream = new MemoryStream();

        // Write WAV header (44 bytes)
        // In production, use proper audio library like NAudio or CSCore
        var header = new byte[44];
        // RIFF header
        header[0] = 0x52; header[1] = 0x49; header[2] = 0x46; header[3] = 0x46;
        // File size (placeholder)
        header[4] = 0; header[5] = 0; header[6] = 0; header[7] = 0;
        // WAVE
        header[8] = 0x57; header[9] = 0x41; header[10] = 0x56; header[11] = 0x45;
        // fmt
        header[12] = 0x66; header[13] = 0x6D; header[14] = 0x74; header[15] = 0x20;
        // Subchunk size
        header[16] = 16; header[17] = 0; header[18] = 0; header[19] = 0;
        // Audio format (PCM)
        header[20] = 1; header[21] = 0;
        // Channels
        header[22] = 2; header[23] = 0;
        // Sample rate (48000)
        header[24] = 0x80; header[25] = 0xBB; header[26] = 0; header[27] = 0;
        // Byte rate
        header[28] = 0; header[29] = 0xEE; header[30] = 2; header[31] = 0;
        // Block align
        header[32] = 4; header[33] = 0;
        // Bits per sample
        header[34] = 16; header[35] = 0;
        // data
        header[36] = 0x64; header[37] = 0x61; header[38] = 0x74; header[39] = 0x61;
        // Data chunk size
        var dataSize = (int)rawData.Length;
        header[40] = (byte)(dataSize & 0xFF);
        header[41] = (byte)((dataSize >> 8) & 0xFF);
        header[42] = (byte)((dataSize >> 16) & 0xFF);
        header[43] = (byte)((dataSize >> 24) & 0xFF);

        wavStream.Write(header, 0, header.Length);
        rawData.CopyTo(wavStream);
        wavStream.Position = 0;

        return wavStream;
    }
}

/// <summary>
/// Tracks the state of a voice connection.
/// </summary>
internal class VoiceConnectionState
{
    public required ulong ChannelId { get; init; }
    public required IAudioClient AudioClient { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required ulong RecordingUserId { get; init; }
}

/// <summary>
/// Buffer for accumulating audio data.
/// </summary>
internal class AudioBuffer
{
    public required ulong UserId { get; init; }
    public required ulong ChannelId { get; init; }
    public required MemoryStream Data { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
}

// Internal messages for VoiceHandlerActor
internal sealed class ProcessVoiceData
{
    public required ulong UserId { get; init; }
    public required byte[] Data { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}

internal sealed class UploadAudioComplete
{
    public required string RecordingId { get; init; }
    public required AudioReference AudioReference { get; init; }
}

internal sealed class VoiceRecordingStarted
{
    public required ulong ChannelId { get; init; }
    public required ulong UserId { get; init; }
}

internal sealed class VoiceConnectionFailed
{
    public required ulong ChannelId { get; init; }
    public required ulong UserId { get; init; }
}

internal sealed class VoiceRecordingStopped
{
    public required ulong ChannelId { get; init; }
}

internal sealed class VoiceRecordingFailed
{
    public required ulong ChannelId { get; init; }
    public required string Error { get; init; }
}
