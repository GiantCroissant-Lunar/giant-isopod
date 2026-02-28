using Godot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using GiantIsopod.Plugin.Actors;

namespace GiantIsopod.Hosts.CompleteApp;

/// <summary>
/// Main scene entry point. Bootstraps the DI container, Akka.NET actor system,
/// and connects it to the Godot viewport via the ECS bridge.
/// </summary>
public partial class Main : Node2D
{
    private ServiceProvider? _services;
    private AgentWorldSystem? _agentWorld;
    private GodotViewportBridge? _viewportBridge;
    private HudController? _hud;
    private ILogger<Main>? _logger;

    public override void _Ready()
    {
        var config = new AgentWorldConfig
        {
            SkillsBasePath = ProjectSettings.GlobalizePath("res://Data/Skills"),
            MemoryBasePath = ProjectSettings.GlobalizePath("user://memory"),
            AgentDataPath = ProjectSettings.GlobalizePath("res://Data/Agents"),
            PiExecutable = "pi",
            MemvidExecutable = "memvid"
        };

        _services = new ServiceCollection()
            .AddLogging(builder => builder
                .SetMinimumLevel(LogLevel.Debug)
                .AddProvider(new GodotLoggerProvider()))
            .AddGiantIsopodPlugins(config)
            .BuildServiceProvider();

        _logger = _services.GetRequiredService<ILogger<Main>>();
        _agentWorld = _services.GetRequiredService<AgentWorldSystem>();
        _viewportBridge = new GodotViewportBridge();

        _agentWorld.SetViewportBridge(_viewportBridge);

        _hud = GetNode<HudController>("HUD/HUDRoot");

        _logger.LogInformation("Giant Isopod ready");
    }

    public override void _Process(double delta)
    {
        if (_viewportBridge == null || _hud == null) return;

        foreach (var evt in _viewportBridge.DrainEvents())
        {
            _hud.ApplyEvent(evt);
        }
    }

    public override void _ExitTree()
    {
        _agentWorld?.Dispose();
        _services?.Dispose();
        _logger?.LogInformation("Giant Isopod shutdown");
    }
}
