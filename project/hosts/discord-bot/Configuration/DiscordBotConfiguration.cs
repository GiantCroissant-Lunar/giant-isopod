namespace GiantIsopod.DiscordBot.Configuration;

/// <summary>
/// Configuration settings for the Discord bot.
/// </summary>
public class DiscordBotConfiguration
{
    public const string SectionName = "DiscordBot";

    /// <summary>
    /// Discord bot token (from environment variable or config).
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Command prefix for text commands.
    /// </summary>
    public string CommandPrefix { get; set; } = "!";

    /// <summary>
    /// Status message displayed by the bot.
    /// </summary>
    public string StatusMessage { get; set; } = "Giant Isopod Bot";
}

/// <summary>
/// Akka.NET actor system configuration.
/// </summary>
public class AkkaConfiguration
{
    public const string SectionName = "Akka";

    public string ActorSystemName { get; set; } = "DiscordBotSystem";

    public AkkaRemoteConfiguration Remote { get; set; } = new();

    public TargetActorConfiguration TargetActor { get; set; } = new();
}

/// <summary>
/// Akka.Remote configuration for remoting.
/// </summary>
public class AkkaRemoteConfiguration
{
    public string Hostname { get; set; } = "0.0.0.0";

    public int Port { get; set; } = 8080;

    public string PublicHostname { get; set; } = "localhost";
}

/// <summary>
/// Target actor configuration for the main application gateway.
/// </summary>
public class TargetActorConfiguration
{
    /// <summary>
    /// Actor selection path to the main app's Discord gateway actor.
    /// </summary>
    public string ActorSelectionPath { get; set; } = "akka.tcp://MainApp@localhost:8090/user/discord-gateway";
}

/// <summary>
/// Storage configuration for audio file storage.
/// </summary>
public class StorageConfiguration
{
    public const string SectionName = "Storage";

    public string Provider { get; set; } = "S3";

    public S3StorageConfiguration S3 { get; set; } = new();
}

/// <summary>
/// S3 storage configuration.
/// </summary>
public class S3StorageConfiguration
{
    public string BucketName { get; set; } = "giant-isopod-audio";

    public string Region { get; set; } = "us-east-1";

    public int PresignedUrlExpiryMinutes { get; set; } = 60;
}

/// <summary>
/// Voice recording configuration.
/// </summary>
public class VoiceConfiguration
{
    public const string SectionName = "Voice";

    public string RecordingFormat { get; set; } = "wav";

    public int MaxRecordingDurationSeconds { get; set; } = 300;

    public int UploadChunkSizeBytes { get; set; } = 1048576;
}
