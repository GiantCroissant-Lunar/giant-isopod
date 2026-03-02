using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using GiantIsopod.DiscordBot.Configuration;
using GiantIsopod.DiscordBot.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace GiantIsopod.DiscordBot.Services;

/// <summary>
/// AWS S3 implementation of the storage service.
/// </summary>
public class S3StorageService : IStorageService
{
    private readonly IAmazonS3 _s3Client;
    private readonly S3StorageConfiguration _config;
    private readonly ILogger<S3StorageService> _logger;

    public S3StorageService(
        IOptions<StorageConfiguration> storageConfig,
        ILogger<S3StorageService> logger)
    {
        _config = storageConfig.Value.S3;
        _logger = logger;

        // Initialize S3 client
        // Credentials can come from environment variables, IAM role, or AWS credentials file
        var config = new AmazonS3Config
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(_config.Region),
            // Allow for localstack/minio testing
            ServiceURL = Environment.GetEnvironmentVariable("AWS_SERVICE_URL")
        };

        var credentials = FallbackCredentialsFactory.GetCredentials();
        _s3Client = new AmazonS3Client(credentials, config);
    }

    public async Task<AudioReference> UploadAudioAsync(
        Stream audioStream,
        string recordingId,
        string format,
        CancellationToken cancellationToken = default)
    {
        var key = GetStorageKey(recordingId, format);
        var contentType = GetContentType(format);

        // Calculate hash for integrity
        string contentHash;
        long fileSize;
        using (var memoryStream = new MemoryStream())
        {
            await audioStream.CopyToAsync(memoryStream, cancellationToken);
            fileSize = memoryStream.Length;
            memoryStream.Position = 0;

            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(memoryStream);
            contentHash = Convert.ToHexString(hash).ToLowerInvariant();
            memoryStream.Position = 0;

            var putRequest = new PutObjectRequest
            {
                BucketName = _config.BucketName,
                Key = key,
                InputStream = memoryStream,
                ContentType = contentType,
                Metadata =
                {
                    ["x-amz-meta-recording-id"] = recordingId,
                    ["x-amz-meta-format"] = format,
                    ["x-amz-meta-uploaded-at"] = DateTimeOffset.UtcNow.ToString("O"),
                    ["x-amz-meta-content-hash"] = contentHash
                }
            };

            _logger.LogInformation("Uploading audio {RecordingId} to S3 bucket {Bucket}",
                recordingId, _config.BucketName);

            await _s3Client.PutObjectAsync(putRequest, cancellationToken);
        }

        // Generate presigned URL
        var expiry = TimeSpan.FromMinutes(_config.PresignedUrlExpiryMinutes);
        var downloadUrl = await GetPresignedUrlAsync(recordingId, expiry, cancellationToken);

        return new AudioReference
        {
            RecordingId = recordingId,
            DownloadUrl = downloadUrl,
            ExpiresAt = DateTimeOffset.UtcNow.Add(expiry),
            Format = format,
            FileSizeBytes = fileSize,
            StorageBucket = _config.BucketName,
            StorageKey = key,
            ContentHash = contentHash
        };
    }

    public Task<string> GetPresignedUrlAsync(
        string recordingId,
        TimeSpan expiry,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(recordingId))
        {
            throw new ArgumentException("Recording ID cannot be null or empty.", nameof(recordingId));
        }

        var key = GetStorageKey(recordingId, "wav"); // Default, could be stored elsewhere

        var request = new GetPreSignedUrlRequest
        {
            BucketName = _config.BucketName,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(expiry)
        };

        var url = _s3Client.GetPreSignedURL(request);
        return Task.FromResult(url);
    }

    public async Task DeleteAudioAsync(
        string recordingId,
        CancellationToken cancellationToken = default)
    {
        var key = GetStorageKey(recordingId, "wav");

        var deleteRequest = new DeleteObjectRequest
        {
            BucketName = _config.BucketName,
            Key = key
        };

        await _s3Client.DeleteObjectAsync(deleteRequest, cancellationToken);
        _logger.LogInformation("Deleted audio {RecordingId} from S3", recordingId);
    }

    public async Task<bool> ExistsAsync(
        string recordingId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var key = GetStorageKey(recordingId, "wav");

            await _s3Client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _config.BucketName,
                Key = key
            }, cancellationToken);

            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static string GetStorageKey(string recordingId, string format)
    {
        // Organize by date prefix for easier management
        var now = DateTime.UtcNow;
        var datePrefix = $"{now:yyyy/MM/dd}";
        return $"recordings/{datePrefix}/{recordingId}.{format}";
    }

    private static string GetContentType(string format)
    {
        return format.ToLowerInvariant() switch
        {
            "wav" => "audio/wav",
            "mp3" => "audio/mpeg",
            "ogg" => "audio/ogg",
            "webm" => "audio/webm",
            "m4a" => "audio/mp4",
            _ => "application/octet-stream"
        };
    }
}
