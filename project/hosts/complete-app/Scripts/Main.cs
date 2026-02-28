using Godot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using GiantIsopod.Contracts.Core;
using GiantIsopod.Plugin.Actors;

namespace GiantIsopod.Hosts.CompleteApp;

/// <summary>
/// Main scene entry point. Bootstraps the DI container, Akka.NET actor system,
/// and connects it to the Godot viewport via the ECS bridge.
/// Auto-discovers agent profiles from Data/Agents and spawns them.
/// </summary>
public partial class Main : Node2D
{
    private ServiceProvider? _services;
    private AgentWorldSystem? _agentWorld;
    private GodotViewportBridge? _viewportBridge;
    private HudController? _hud;
    private ILogger<Main>? _logger;
    private Node2D? _agentsNode;

    private readonly Dictionary<string, AgentSprite> _agentSprites = new();

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
        _agentsNode = GetNode<Node2D>("World/Agents");

        // Draw a subtle grid background
        QueueRedraw();

        // Auto-discover and spawn agents from Data/Agents
        SpawnAgentsFromData(config);

        _logger.LogInformation("Giant Isopod ready");
    }

    public override void _Process(double delta)
    {
        if (_viewportBridge == null || _hud == null) return;

        foreach (var evt in _viewportBridge.DrainEvents())
        {
            _hud.ApplyEvent(evt);
            HandleWorldEvent(evt);
        }
    }

    public override void _Draw()
    {
        // Dark background
        var viewport = GetViewportRect();
        DrawRect(viewport, new Color(0.08f, 0.09f, 0.12f));

        // Subtle grid
        var gridColor = new Color(0.14f, 0.15f, 0.19f);
        float step = 64f;
        for (float x = 0; x < viewport.Size.X; x += step)
            DrawLine(new Vector2(x, 0), new Vector2(x, viewport.Size.Y), gridColor);
        for (float y = 0; y < viewport.Size.Y; y += step)
            DrawLine(new Vector2(0, y), new Vector2(viewport.Size.X, y), gridColor);
    }

    private void HandleWorldEvent(ViewportEvent evt)
    {
        switch (evt)
        {
            case AgentSpawnedEvent spawned:
                CreateAgentSprite(spawned.AgentId, spawned.VisualInfo);
                break;
            case AgentDespawnedEvent despawned:
                RemoveAgentSprite(despawned.AgentId);
                break;
            case StateChangedEvent stateChanged:
                if (_agentSprites.TryGetValue(stateChanged.AgentId, out var sprite))
                    sprite.SetActivityState(stateChanged.State);
                break;
        }
    }

    private void CreateAgentSprite(string agentId, AgentVisualInfo info)
    {
        if (_agentsNode == null || _agentSprites.ContainsKey(agentId)) return;

        var sprite = new AgentSprite(agentId, info);
        var viewport = GetViewportRect();
        // Place agents in the center area with some spread
        var rng = new RandomNumberGenerator();
        rng.Seed = (ulong)agentId.GetHashCode();
        sprite.Position = new Vector2(
            rng.RandfRange(200, viewport.Size.X - 440),
            rng.RandfRange(150, viewport.Size.Y - 250));

        _agentsNode.AddChild(sprite);
        _agentSprites[agentId] = sprite;
    }

    private void RemoveAgentSprite(string agentId)
    {
        if (_agentSprites.TryGetValue(agentId, out var sprite))
        {
            sprite.QueueFree();
            _agentSprites.Remove(agentId);
        }
    }

    private void SpawnAgentsFromData(AgentWorldConfig config)
    {
        if (_agentWorld == null) return;

        // Use res:// path for Godot's virtual filesystem (works in both editor and export)
        const string agentResDir = "res://Data/Agents";

        if (!DirAccess.DirExistsAbsolute(agentResDir))
        {
            _logger?.LogWarning("Agent data directory not found: {Path}", agentResDir);
            return;
        }

        using var dir = DirAccess.Open(agentResDir);
        if (dir == null)
        {
            _logger?.LogWarning("Could not open agent data directory: {Path}", agentResDir);
            return;
        }

        dir.ListDirBegin();
        var fileName = dir.GetNext();
        while (!string.IsNullOrEmpty(fileName))
        {
            if (fileName.EndsWith(".aieos.json"))
            {
                var agentId = fileName.Replace(".aieos.json", "");
                var resPath = $"{agentResDir}/{fileName}";

                // Read the JSON from Godot's virtual filesystem (works inside PCK)
                string? profileJson = null;
                using var file = Godot.FileAccess.Open(resPath, Godot.FileAccess.ModeFlags.Read);
                if (file != null)
                {
                    profileJson = file.GetAsText();
                }

                _logger?.LogInformation("Auto-spawning agent: {AgentId}", agentId);

                _agentWorld.AgentSupervisor.Tell(
                    new SpawnAgent(agentId, profileJson ?? "", "builder"),
                    Akka.Actor.ActorRefs.NoSender);
            }
            fileName = dir.GetNext();
        }
        dir.ListDirEnd();
    }

    public override void _ExitTree()
    {
        _agentWorld?.Dispose();
        _services?.Dispose();
        _logger?.LogInformation("Giant Isopod shutdown");
    }
}
