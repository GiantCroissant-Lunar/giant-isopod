# Giant Isopod Discord Bot Host

A .NET 9 Discord bot built with Akka.NET actors for scalable, concurrent user session management and Akka.Remote for integration with the main application.

## Features

- **Discord Integration**: Full Discord bot with text and voice message support
- **Actor-Based Architecture**: Akka.NET actors for robust concurrent session management
- **A2A-Style Messaging**: Uses references (URLs) instead of blobs for audio data
- **Streaming Responses**: Supports streaming text responses back to Discord
- **Cloud Storage**: S3 integration for audio file storage with presigned URLs
- **Remote Communication**: Akka.Remote for seamless integration with the main app

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                         Giant Isopod Discord Bot Host                       │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  ┌─────────────────┐      ┌─────────────────┐      ┌─────────────────┐    │
│  │  DiscordBotActor│──────│  SessionActor   │──────│ SessionActor    │    │
│  │  (Main Gateway) │      │  (User 1)       │      │ (User N)        │    │
│  └────────┬────────┘      └────────┬────────┘      └─────────────────┘    │
│           │                        │                                        │
│           │            ┌───────────┴───────────┐                          │
│           │            │                       │                          │
│           │     ┌──────▼──────┐       ┌───────▼────────┐                 │
│           │     │VoiceHandler │       │ Akka.Remote    │                 │
│           │     │Actor        │       │ (to Main App)  │                 │
│           │     └──────┬──────┘       └────────┬───────┘                 │
│           │            │                       │                          │
│           └────────────┴───────────────────────┘                          │
│                        │                                                    │
│  ┌─────────────────────▼────────────────────────┐                          │
│  │           S3 Storage Service                │                          │
│  │  (Audio files with presigned URLs)          │                          │
│  └──────────────────────────────────────────────┘                          │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
                                     │
                                     │ Akka.Remote
                                     ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                      Main Application (External)                            │
│                    (akka.tcp://MainApp@host:port)                           │
└─────────────────────────────────────────────────────────────────────────────┘
```

## Project Structure

```
project/hosts/discord-bot/
├── GiantIsopod.DiscordBot.csproj    # Project file with dependencies
├── Program.cs                        # Host setup, DI, Akka configuration
├── appsettings.json                  # Production configuration
├── appsettings.Development.json      # Development configuration
├── Configuration/
│   └── DiscordBotConfiguration.cs    # Configuration classes
├── Actors/
│   ├── DiscordBotActor.cs            # Main bot actor (Discord gateway)
│   ├── SessionActor.cs               # Per-user session management
│   └── VoiceHandlerActor.cs          # Voice recording and upload
├── Services/
│   ├── IDiscordBotService.cs         # Discord service interface
│   ├── DiscordBotService.cs          # Discord client management
│   ├── IStorageService.cs            # Storage service interface
│   └── S3StorageService.cs           # AWS S3 implementation
└── Models/
    ├── DiscordPromptReceived.cs      # A2A-style prompt message
    ├── AgentRequest.cs               # Request to main app
    ├── AgentResponse.cs              # Response from main app
    └── AudioReference.cs             # Audio reference (A2A pattern)
```

## Prerequisites

- .NET 9 SDK
- Discord bot token (from [Discord Developer Portal](https://discord.com/developers/applications))
- AWS credentials (for S3 storage) OR Firebase credentials
- Main application with Akka.Remote endpoint configured

## Configuration

### Environment Variables

| Variable | Description | Required |
|----------|-------------|----------|
| `DISCORD_BOT_TOKEN` | Discord bot token | Yes |
| `AWS_ACCESS_KEY_ID` | AWS access key for S3 | Yes* |
| `AWS_SECRET_ACCESS_KEY` | AWS secret key for S3 | Yes* |
| `AWS_REGION` | AWS region (default: us-east-1) | No |
| `AWS_SERVICE_URL` | Custom S3 endpoint (for MinIO/localstack) | No |

*Required if using S3 storage

### appsettings.json

```json
{
  "DiscordBot": {
    "Token": "${DISCORD_BOT_TOKEN}",
    "CommandPrefix": "!",
    "StatusMessage": "Giant Isopod Bot Online"
  },
  "Akka": {
    "ActorSystemName": "DiscordBotSystem",
    "Remote": {
      "Hostname": "0.0.0.0",
      "Port": 8080,
      "PublicHostname": "localhost"
    },
    "TargetActor": {
      "ActorSelectionPath": "akka.tcp://MainApp@localhost:8090/user/discord-gateway"
    }
  },
  "Storage": {
    "Provider": "S3",
    "S3": {
      "BucketName": "giant-isopod-audio",
      "Region": "us-east-1",
      "PresignedUrlExpiryMinutes": 60
    }
  },
  "Voice": {
    "RecordingFormat": "wav",
    "MaxRecordingDurationSeconds": 300
  }
}
```

## Building

```bash
# Restore dependencies
dotnet restore

# Build the project
dotnet build

# Run in development mode
dotnet run

# Run in production mode
ASPNETCORE_ENVIRONMENT=Production dotnet run
```

## Running

### Development

```bash
cd project/hosts/discord-bot
dotnet run
```

### Production

```bash
# Set environment variables
export DISCORD_BOT_TOKEN="your-bot-token"
export AWS_ACCESS_KEY_ID="your-aws-key"
export AWS_SECRET_ACCESS_KEY="your-aws-secret"

dotnet run --configuration Release
```

### Docker (Optional)

```bash
docker build -t giant-isopod-discord-bot .
docker run -e DISCORD_BOT_TOKEN=xxx -e AWS_ACCESS_KEY_ID=xxx giant-isopod-discord-bot
```

## Message Flow

### Text Message Flow

1. Discord user sends text message
2. [`DiscordBotService`](Services/DiscordBotService.cs:1) receives message via Discord.Net
3. [`DiscordBotActor`](Actors/DiscordBotActor.cs:1) creates/retrieves [`SessionActor`](Actors/SessionActor.cs:1) for user
4. [`SessionActor`](Actors/SessionActor.cs:1) sends [`AgentRequest`](Models/AgentRequest.cs:1) to main app via Akka.Remote
5. Main app processes and sends streaming [`AgentResponse`](Models/AgentResponse.cs:1) back
6. [`SessionActor`](Actors/SessionActor.cs:1) updates Discord message with streamed content

### Voice Message Flow

1. Discord user joins voice channel and speaks
2. [`VoiceHandlerActor`](Actors/VoiceHandlerActor.cs:1) records audio stream
3. Audio is converted to WAV and uploaded to S3 via [`S3StorageService`](Services/S3StorageService.cs:1)
4. [`AudioReference`](Models/AudioReference.cs:1) (with presigned URL) is sent to main app via [`AgentRequest`](Models/AgentRequest.cs:1)
5. Main app downloads audio from S3 URL for processing
6. Text response is streamed back to Discord

## A2A-Style Messaging

Following the A2A (Agent-to-Agent) pattern, this bot uses references instead of embedding large data blobs in messages:

- **Audio**: Stored in S3, only [`AudioReference`](Models/AudioReference.cs:1) (URL + metadata) is sent via Akka
- **Benefits**: Smaller messages, better performance, external storage lifecycle management

```csharp
public class AudioReference
{
    public string RecordingId { get; init; }
    public string DownloadUrl { get; init; }  // Presigned S3 URL
    public string Format { get; init; }
    public double? DurationSeconds { get; init; }
    // ... metadata only, no audio data
}
```

## Correlation IDs

All requests use correlation IDs for tracking:

```csharp
var correlationId = Guid.NewGuid().ToString("N");
var request = new AgentRequest
{
    CorrelationId = correlationId,
    // ...
};
```

Responses from the main app include the same correlation ID, enabling proper request/response pairing.

## Actor Hierarchy

```
/user/discord-bot              (DiscordBotActor - singleton)
    ├── session-{userId}-{channelId}   (SessionActor - per user/channel)
    └── voice-{guildId}                (VoiceHandlerActor - per guild)
```

## Integration with Main App

The bot connects to the main application via Akka.Remote. The main app should have:

1. An actor at the configured `TargetActor.ActorSelectionPath`
2. Ability to receive [`AgentRequest`](Models/AgentRequest.cs:1) messages
3. Ability to send [`AgentResponse`](Models/AgentResponse.cs:1) messages back
4. HTTP client to download audio from presigned S3 URLs

Example main app configuration:

```csharp
// In main app
var discordGateway = system.ActorOf<DiscordGatewayActor>("discord-gateway");

// Akka.Remote configuration
akka {
    actor {
        provider = remote
    }
    remote {
        dot-netty.tcp {
            hostname = "localhost"
            port = 8090
        }
    }
}
```

## Troubleshooting

### Bot won't connect

- Verify `DISCORD_BOT_TOKEN` is set correctly
- Check Discord Gateway intents are enabled in Developer Portal
- Review logs for connection errors

### Akka.Remote connection issues

- Verify main app is running and accessible
- Check firewall rules for Akka ports
- Ensure `TargetActor.ActorSelectionPath` is correct

### S3 upload failures

- Verify AWS credentials are set
- Check S3 bucket exists and is accessible
- Review IAM permissions for PutObject and GetObject

### Voice recording issues

- Bot needs `Connect` and `Speak` voice permissions in Discord
- Opus library may be required for audio encoding/decoding
- Check voice channel bitrate limits

## License

MIT License - Part of the Giant Isopod project.
