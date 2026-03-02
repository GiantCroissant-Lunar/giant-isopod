using Akka.Actor;
using Akka.Configuration;
using GiantIsopod.DiscordBot.Actors;
using GiantIsopod.DiscordBot.Configuration;
using GiantIsopod.DiscordBot.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GiantIsopod.DiscordBot;

/// <summary>
/// Entry point for the Giant Isopod Discord Bot Host.
/// Sets up the .NET generic host, Akka.NET actor system, and Discord integration.
/// </summary>
public class Program
{
    public static async Task Main(string[] args)
    {
        var host = CreateHostBuilder(args).Build();

        // Get the logger for startup messages
        var logger = host.Services.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("Giant Isopod Discord Bot Host starting...");

        await host.RunAsync();
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((context, config) =>
            {
                var env = context.HostingEnvironment;

                config.SetBasePath(Directory.GetCurrentDirectory());
                config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                config.AddJsonFile($"appsettings.{env.EnvironmentName}.json", optional: true, reloadOnChange: true);
                config.AddEnvironmentVariables();

                // Support for user secrets in development
                if (env.IsDevelopment())
                {
                    config.AddUserSecrets<Program>();
                }
            })
            .ConfigureServices((context, services) =>
            {
                var configuration = context.Configuration;

                // Bind configuration sections
                services.Configure<DiscordBotConfiguration>(
                    configuration.GetSection(DiscordBotConfiguration.SectionName));
                services.Configure<AkkaConfiguration>(
                    configuration.GetSection(AkkaConfiguration.SectionName));
                services.Configure<StorageConfiguration>(
                    configuration.GetSection(StorageConfiguration.SectionName));
                services.Configure<VoiceConfiguration>(
                    configuration.GetSection(VoiceConfiguration.SectionName));

                // Register services
                services.AddSingleton<IDiscordBotService, DiscordBotService>();
                services.AddSingleton<IStorageService, S3StorageService>();

                // Register Akka Actor System
                services.AddSingleton<ActorSystem>(sp =>
                {
                    var akkaConfig = sp.GetRequiredService<IOptions<AkkaConfiguration>>().Value;
                    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

                    return CreateActorSystem(akkaConfig, loggerFactory);
                });

                // Register the root Discord bot actor
                services.AddSingleton<IActorRef>(sp =>
                {
                    var system = sp.GetRequiredService<ActorSystem>();
                    var discordService = sp.GetRequiredService<IDiscordBotService>();
                    var akkaConfig = sp.GetRequiredService<IOptions<AkkaConfiguration>>();
                    var logger = sp.GetRequiredService<ILogger<DiscordBotActor>>();

                    var props = Props.Create(() => new DiscordBotActor(
                        discordService,
                        akkaConfig,
                        logger));

                    return system.ActorOf(props, "discord-bot");
                });

                // Register hosted service to manage bot lifecycle
                services.AddHostedService<DiscordBotHostedService>();
            })
            .ConfigureLogging((context, logging) =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.AddDebug();

                var env = context.HostingEnvironment;
                if (env.IsDevelopment())
                {
                    logging.SetMinimumLevel(LogLevel.Debug);
                }
                else
                {
                    logging.SetMinimumLevel(LogLevel.Information);
                }
            });

    /// <summary>
    /// Creates and configures the Akka.NET Actor System with remoting enabled.
    /// </summary>
    private static ActorSystem CreateActorSystem(
        AkkaConfiguration config,
        ILoggerFactory loggerFactory)
    {
        // Build HOCON configuration for Akka.Remote
        var hocon = $@"
            akka {{
                actor {{
                    provider = remote
                    serializers {{
                        hyperion = ""Akka.Serialization.HyperionSerializer, Akka.Serialization.Hyperion""
                    }}
                    serialization-bindings {{
                        ""System.Object"" = hyperion
                    }}
                }}
                remote {{
                    dot-netty.tcp {{
                        port = {config.Remote.Port}
                        hostname = ""{config.Remote.Hostname}""
                        public-hostname = ""{config.Remote.PublicHostname}""

                        # Connection settings
                        send-buffer-size = 512000b
                        receive-buffer-size = 512000b
                        maximum-frame-size = 512000b

                        # Reconnection settings
                        connection-timeout = 15s
                    }}
                }}
                loglevel = INFO
                log-config-on-start = off
            }}
        ";

        var akkaConfigObj = ConfigurationFactory.ParseString(hocon);
        var system = ActorSystem.Create(config.ActorSystemName, akkaConfigObj);

        // Set up logging adapter
        var log = loggerFactory.CreateLogger<ActorSystem>();
        log.LogInformation(
            "Akka.NET Actor System '{ActorSystem}' started with remoting on {Host}:{Port}",
            config.ActorSystemName,
            config.Remote.Hostname,
            config.Remote.Port);

        return system;
    }
}

/// <summary>
/// Hosted service that manages the Discord bot lifecycle and coordinates with the actor system.
/// </summary>
public class DiscordBotHostedService : IHostedService, IDisposable
{
    private readonly ActorSystem _actorSystem;
    private readonly IActorRef _discordBotActor;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DiscordBotHostedService> _logger;
    private readonly IHostEnvironment _environment;
    private bool _isDisposed;

    public DiscordBotHostedService(
        ActorSystem actorSystem,
        IActorRef discordBotActor,
        IServiceProvider serviceProvider,
        ILogger<DiscordBotHostedService> logger,
        IHostEnvironment environment)
    {
        _actorSystem = actorSystem;
        _discordBotActor = discordBotActor;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _environment = environment;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Starting Discord Bot Hosted Service in {Environment} mode...",
            _environment.EnvironmentName);

        try
        {
            // Start the Discord bot actor
            _discordBotActor.Tell(new StartBot());

            // Give the bot a moment to connect
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

            _logger.LogInformation("Discord Bot Hosted Service started successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start Discord Bot Hosted Service");
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Discord Bot Hosted Service...");

        try
        {
            // Stop the Discord bot actor gracefully with timeout
            var stopTask = _discordBotActor.Ask<BotStopped>(new StopBot(), TimeSpan.FromSeconds(10));

            // Wait for stop or timeout
            var completedTask = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(15), cancellationToken));

            if (completedTask == stopTask)
            {
                _logger.LogInformation("Discord bot actor stopped gracefully");
            }
            else
            {
                _logger.LogWarning("Discord bot actor stop timed out, forcing termination");
            }

            // Terminate the actor system
            await _actorSystem.Terminate();

            _logger.LogInformation("Discord Bot Hosted Service stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Discord Bot Hosted Service shutdown");
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        _actorSystem?.Dispose();
        _isDisposed = true;

        GC.SuppressFinalize(this);
    }
}
