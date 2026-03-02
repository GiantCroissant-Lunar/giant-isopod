namespace GiantIsopod.DiscordBot.Models;

/// <summary>
/// A2A-style reference to audio content stored remotely.
/// Follows the pattern: store large blobs externally, pass references through messages.
/// </summary>
public sealed class AudioReference
{
    /// <summary>
    /// Unique identifier for the audio recording.
    /// </summary>
    public required string RecordingId { get; init; }

    /// <summary>
    /// Presigned URL for downloading the audio file.
    /// </summary>
    public required string DownloadUrl { get; init; }

    /// <summary>
    /// URL expiry timestamp.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// Audio format (wav, mp3, ogg, etc.).
    /// </summary>
    public required string Format { get; init; }

    /// <summary>
    /// Duration of the audio in seconds.
    /// </summary>
    public double? DurationSeconds { get; init; }

    /// <summary>
    /// File size in bytes.
    /// </summary>
    public long? FileSizeBytes { get; init; }

    /// <summary>
    /// Sample rate in Hz.
    /// </summary>
    public int? SampleRate { get; init; }

    /// <summary>
    /// Number of audio channels.
    /// </summary>
    public int? Channels { get; init; }

    /// <summary>
    /// S3 bucket or storage location.
    /// </summary>
    public string? StorageBucket { get; init; }

    /// <summary>
    /// Storage key/path for the audio file.
    /// </summary>
    public string? StorageKey { get; init; }

    /// <summary>
    /// MD5 hash for integrity verification.
    /// </summary>
    public string? ContentHash { get; init; }
}

/// <summary>
/// Command to initiate voice recording.
/// </summary>
public sealed class StartVoiceRecording
{
    public required ulong GuildId { get; init; }

    public required ulong ChannelId { get; init; }

    public required ulong UserId { get; init; }

    public int? MaxDurationSeconds { get; init; }
}

/// <summary>
/// Command to stop voice recording.
/// </summary>
public sealed class StopVoiceRecording
{
    public required ulong GuildId { get; init; }

    public required ulong ChannelId { get; init; }
}

/// <summary>
/// Result of a completed voice recording.
/// </summary>
public sealed class VoiceRecordingResult
{
    public required string RecordingId { get; init; }

    public required AudioReference AudioReference { get; init; }

    public required ulong UserId { get; init; }

    public DateTimeOffset RecordedAt { get; init; } = DateTimeOffset.UtcNow;

    public double DurationSeconds { get; init; }
}
