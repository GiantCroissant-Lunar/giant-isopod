using GiantIsopod.DiscordBot.Models;

namespace GiantIsopod.DiscordBot.Services;

/// <summary>
/// Interface for audio file storage services.
/// Supports S3, Firebase, or other cloud storage providers.
/// </summary>
public interface IStorageService
{
    /// <summary>
    /// Uploads audio data to storage and returns a reference.
    /// </summary>
    Task<AudioReference> UploadAudioAsync(
        Stream audioStream,
        string recordingId,
        string format,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a presigned URL for downloading the audio.
    /// </summary>
    Task<string> GetPresignedUrlAsync(
        string recordingId,
        TimeSpan expiry,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an audio file from storage.
    /// </summary>
    Task DeleteAudioAsync(
        string recordingId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if an audio file exists in storage.
    /// </summary>
    Task<bool> ExistsAsync(
        string recordingId,
        CancellationToken cancellationToken = default);
}
