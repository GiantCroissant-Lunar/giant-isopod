using Godot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using GiantIsopod.Contracts.Core;
using GiantIsopod.Contracts.ECS;
using GiantIsopod.Plugin.Actors;
using GiantIsopod.Plugin.ECS;

namespace GiantIsopod.Hosts.CompleteApp;

/// <summary>
/// Main scene entry point. Bootstraps DI, Akka.NET actor system, and Friflo ECS.
/// Viewport events → ECS entities. ECS systems tick each frame. ECS state → Godot nodes.
/// </summary>
public partial class Main : Node2D
{
    private ServiceProvider? _services;
    private AgentWorldSystem? _agentWorld;
    private GodotViewportBridge? _viewportBridge;
    private HudController? _hud;
    private TaskGraphView? _taskGraphView;
    private HudHotReload? _hotReload;
    private ILogger<Main>? _logger;
    private Node2D? _agentsNode;
    private AgentEcsWorld? _ecsWorld;

    // ECS entity → Godot sprite mapping
    private readonly Dictionary<string, AgentSprite> _sprites = new();

    // Loaded AIEOS profiles for spawning new agents
    private readonly Dictionary<string, string> _profileJsonCache = new();
    private int _nextAgentIndex;

    public override void _Ready()
    {
        // Load runtimes — try new format first, fall back to legacy cli-providers.json
        GiantIsopod.Plugin.Process.RuntimeRegistry runtimes;
        const string runtimesResPath = "res://Data/Runtimes/runtimes.json";
        const string legacyResPath = "res://Data/CliProviders/cli-providers.json";

        using (var file = Godot.FileAccess.Open(runtimesResPath, Godot.FileAccess.ModeFlags.Read))
        {
            if (file != null)
            {
                runtimes = GiantIsopod.Plugin.Process.RuntimeRegistry.LoadFromJson(file.GetAsText());
            }
            else
            {
                var legacyJson = "{}";
                using (var legacyFile = Godot.FileAccess.Open(legacyResPath, Godot.FileAccess.ModeFlags.Read))
                {
                    if (legacyFile != null)
                        legacyJson = legacyFile.GetAsText();
                }
                runtimes = GiantIsopod.Plugin.Process.RuntimeRegistry.LoadFromLegacyCliProviders(legacyJson);
            }
        }

        var config = new AgentWorldConfig
        {
            SkillsBasePath = ProjectSettings.GlobalizePath("res://Data/Skills"),
            MemoryBasePath = ProjectSettings.GlobalizePath("user://memory"),
            AgentDataPath = ProjectSettings.GlobalizePath("res://Data/Agents"),
            Runtimes = runtimes,
            DefaultRuntimeId = "pi",
            RuntimeWorkingDirectory = @"C:\lunar-horse\yokan-projects\giant-isopod",
            RuntimeEnvironment = new Dictionary<string, string>
            {
                ["ZAI_API_KEY"] = "08bbb0b6b8d649fbbafa5c11091e5ac3.4dzlUajBX9I8oE0F"
            },
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
        _ecsWorld = new AgentEcsWorld();

        _agentWorld.SetViewportBridge(_viewportBridge);

        _hud = GetNode<HudController>("HUD/HUDRoot");
        _agentsNode = GetNode<Node2D>("World/Agents");

        // Wire HUD buttons
        _hud.OnSpawnRequested += HandleSpawnRequest;
        _hud.OnRemoveRequested += HandleRemoveRequest;
        _hud.OnGenUIActionTriggered += HandleGenUIAction;

        // Populate runtime dropdown
        _hud.SetRuntimes(runtimes.All);

        // Task graph DAG visualization — must live in HUD CanvasLayer for proper UI rendering
        _taskGraphView = new TaskGraphView
        {
            AnchorLeft = 0,
            AnchorTop = 0,
            AnchorRight = 1,
            AnchorBottom = 1,
            OffsetLeft = 0,
            OffsetTop = 0,
            OffsetRight = 0,
            OffsetBottom = 0,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };

        if (_hud.TaskGraphSlot != null)
        {
            _hud.TaskGraphSlot.AddChild(_taskGraphView);
        }
        else
        {
            var hudRoot = GetNode<Control>("HUD/HUDRoot");
            hudRoot.AddChild(_taskGraphView);
        }

        // Hot-reload support (debug builds only)
        _hotReload = new HudHotReload(_hud);

        QueueRedraw();
        CacheAgentProfiles();

        _logger.LogInformation("Giant Isopod ready (ECS)");
    }

    public override void _Process(double delta)
    {
        if (_viewportBridge == null || _hud == null || _ecsWorld == null) return;

        // 1. Drain actor events → update ECS + HUD + task graph view
        foreach (var evt in _viewportBridge.DrainEvents())
        {
            _hud.ApplyEvent(evt);
            ApplyEventToEcs(evt);
            ApplyEventToTaskGraphView(evt);
        }

        // 2. Tick ECS systems (movement, animation, wander)
        _ecsWorld.Tick((float)delta);

        // 3. Sync ECS state → Godot sprites
        SyncSprites();
    }

    public override void _Draw()
    {
        var viewport = GetViewportRect();
        DrawRect(viewport, new Color(0.08f, 0.09f, 0.12f));

        var gridColor = new Color(0.14f, 0.15f, 0.19f);
        float step = 64f;
        for (float x = 0; x < viewport.Size.X; x += step)
            DrawLine(new Vector2(x, 0), new Vector2(x, viewport.Size.Y), gridColor);
        for (float y = 0; y < viewport.Size.Y; y += step)
            DrawLine(new Vector2(0, y), new Vector2(viewport.Size.X, y), gridColor);
    }

    private void ApplyEventToEcs(ViewportEvent evt)
    {
        if (_ecsWorld == null) return;

        switch (evt)
        {
            case AgentSpawnedEvent spawned:
                _ecsWorld.SpawnAgent(spawned.AgentId, spawned.VisualInfo);
                break;

            case AgentDespawnedEvent despawned:
                _ecsWorld.RemoveAgent(despawned.AgentId);
                if (_sprites.TryGetValue(despawned.AgentId, out var spr))
                {
                    spr.AgentClicked -= OnAgentClicked;
                    spr.QueueFree();
                    _sprites.Remove(despawned.AgentId);
                }
                break;

            case StateChangedEvent stateChanged:
                var activity = stateChanged.State switch
                {
                    AgentActivityState.Idle => Activity.Idle,
                    AgentActivityState.Walking => Activity.Walking,
                    AgentActivityState.Typing => Activity.Typing,
                    AgentActivityState.Reading => Activity.Reading,
                    AgentActivityState.Waiting => Activity.Waiting,
                    AgentActivityState.Thinking => Activity.Thinking,
                    _ => Activity.Idle
                };
                _ecsWorld.SetAgentActivity(stateChanged.AgentId, activity);
                break;
        }
    }

    private void ApplyEventToTaskGraphView(ViewportEvent evt)
    {
        if (_taskGraphView == null) return;

        switch (evt)
        {
            case TaskGraphSubmittedEvent submitted:
                _taskGraphView.HandleGraphSubmitted(submitted.GraphId, submitted.Nodes, submitted.Edges);
                break;

            case TaskNodeStatusChangedEvent statusChanged:
                _taskGraphView.HandleNodeStatusChanged(
                    statusChanged.GraphId, statusChanged.TaskId,
                    statusChanged.Status, statusChanged.AssignedAgentId);
                break;

            case TaskGraphCompletedEvent completed:
                _taskGraphView.HandleGraphCompleted(completed.GraphId, completed.Results);
                break;
        }
    }

    private void SyncSprites()
    {
        if (_ecsWorld == null || _agentsNode == null) return;

        _ecsWorld.ForEachAgent((agentId, pos, vis, act, identity) =>
        {
            if (!_sprites.TryGetValue(agentId, out var sprite))
            {
                // Create Godot node for new ECS entity
                sprite = new AgentSprite(agentId, new AgentVisualInfo(agentId, identity.DisplayName));
                _agentsNode.AddChild(sprite);
                sprite.AgentClicked += OnAgentClicked;
                _sprites[agentId] = sprite;
            }

            // Sync ECS position → Godot position
            sprite.Position = new Vector2(pos.X, pos.Y);

            // Sync activity state
            var godotState = act.Current switch
            {
                Activity.Idle => AgentActivityState.Idle,
                Activity.Walking => AgentActivityState.Walking,
                Activity.Typing => AgentActivityState.Typing,
                Activity.Reading => AgentActivityState.Reading,
                Activity.Waiting => AgentActivityState.Waiting,
                Activity.Thinking => AgentActivityState.Thinking,
                _ => AgentActivityState.Idle
            };
            sprite.SyncFromEcs(godotState, vis.AnimationFrame, vis.Facing);
        });
    }

    private void OnAgentClicked(string agentId)
    {
        _hud?.SelectAgent(agentId);
    }


    private void HandleSpawnRequest(string runtimeId)
    {
        if (_agentWorld == null) return;

        _nextAgentIndex++;
        var agentId = $"{runtimeId}-{_nextAgentIndex}";

        // Pick a random cached profile or use a minimal default
        string profileJson = "{}";
        if (_profileJsonCache.Count > 0)
        {
            var profiles = _profileJsonCache.Values.ToArray();
            profileJson = profiles[_nextAgentIndex % profiles.Length];
        }

        _logger?.LogInformation("Spawning agent: {AgentId} (runtime: {RuntimeId})", agentId, runtimeId);
        _agentWorld.AgentSupervisor.Tell(
            new SpawnAgent(agentId, profileJson, "builder", RuntimeId: runtimeId),
            Akka.Actor.ActorRefs.NoSender);
    }

    private void HandleRemoveRequest(string agentId)
    {
        if (_agentWorld == null) return;

        _logger?.LogInformation("Removing agent: {AgentId}", agentId);
        _agentWorld.AgentSupervisor.Tell(
            new StopAgent(agentId),
            Akka.Actor.ActorRefs.NoSender);
    }

    private void CacheAgentProfiles()
    {
        const string agentResDir = "res://Data/Agents";
        if (!DirAccess.DirExistsAbsolute(agentResDir)) return;

        using var dir = DirAccess.Open(agentResDir);
        if (dir == null) return;

        dir.ListDirBegin();
        var fileName = dir.GetNext();
        while (!string.IsNullOrEmpty(fileName))
        {
            if (fileName.EndsWith(".aieos.json"))
            {
                var resPath = $"{agentResDir}/{fileName}";
                using var file = Godot.FileAccess.Open(resPath, Godot.FileAccess.ModeFlags.Read);
                if (file != null)
                {
                    var agentId = fileName.Replace(".aieos.json", "");
                    _profileJsonCache[agentId] = file.GetAsText();
                }
            }
            fileName = dir.GetNext();
        }
        dir.ListDirEnd();
    }

    private void SpawnInitialAgents()
    {
        if (_agentWorld == null) return;

        foreach (var (agentId, json) in _profileJsonCache)
        {
            _logger?.LogInformation("Auto-spawning agent: {AgentId}", agentId);
            _agentWorld.AgentSupervisor.Tell(
                new SpawnAgent(agentId, json, "builder"),
                Akka.Actor.ActorRefs.NoSender);
            _nextAgentIndex++;
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true } keyEvent)
        {
            switch (keyEvent.Keycode)
            {
                case Key.F5:
                    SubmitDemoTaskGraph();
                    GetViewport().SetInputAsHandled();
                    break;
                case Key.F6:
                    _hotReload?.ReloadHud();
                    _logger?.LogInformation("HUD reloaded (F6)");
                    GetViewport().SetInputAsHandled();
                    break;
                case Key.F7:
                    SubmitDemoGenUI();
                    GetViewport().SetInputAsHandled();
                    break;
            }
        }
    }

    private int _demoGraphIndex;

    private void SubmitDemoTaskGraph()
    {
        if (_agentWorld == null) return;

        _demoGraphIndex++;
        var graphId = $"demo-{_demoGraphIndex}";

        var nodes = new List<TaskNode>
        {
            new("analyze", "Analyze the request and extract requirements",
                new HashSet<string> { "analysis" }),
            new("research", "Research relevant documentation and prior art",
                new HashSet<string> { "research" }),
            new("plan", "Create implementation plan from analysis and research",
                new HashSet<string> { "planning" }),
            new("implement", "Write the code changes",
                new HashSet<string> { "coding" }),
            new("test", "Run tests and verify correctness",
                new HashSet<string> { "testing" }),
            new("review", "Review final output and summarize",
                new HashSet<string> { "review" }),
        };

        var edges = new List<TaskEdge>
        {
            new("analyze", "plan"),
            new("research", "plan"),
            new("plan", "implement"),
            new("implement", "test"),
            new("test", "review"),
        };

        _logger?.LogInformation("Submitting demo task graph: {GraphId} ({NodeCount} nodes, {EdgeCount} edges)",
            graphId, nodes.Count, edges.Count);

        _agentWorld.TaskGraph.Tell(
            new SubmitTaskGraph(graphId, nodes, edges),
            Akka.Actor.ActorRefs.NoSender);
    }

    private void HandleGenUIAction(string agentId, string surfaceId, string actionId, string componentId)
    {
        _logger?.LogInformation("[GenUI] Action from {AgentId}: surface={SurfaceId} action={ActionId} component={ComponentId}",
            agentId, surfaceId, actionId, componentId);

        // Forward to agent actor as GenUIAction message
        if (_agentWorld != null)
        {
            _agentWorld.AgentSupervisor.Tell(
                new GenUIAction(agentId, surfaceId, actionId, componentId),
                Akka.Actor.ActorRefs.NoSender);
        }
    }

    private void SubmitDemoGenUI()
    {
        // Load demo A2UI form JSON and render it
        const string demoPath = "res://Data/Demo/demo-a2ui-form.json";
        using var file = Godot.FileAccess.Open(demoPath, Godot.FileAccess.ModeFlags.Read);
        if (file == null)
        {
            _logger?.LogWarning("Demo A2UI form not found at {Path}", demoPath);
            return;
        }

        var json = file.GetAsText();
        _logger?.LogInformation("Rendering demo A2UI form (F7)");

        // Send through viewport bridge so it goes through normal HUD event flow
        _viewportBridge?.PublishGenUIRequest("demo-agent", json);
    }

    public override void _ExitTree()
    {
        if (_hud != null)
        {
            _hud.OnSpawnRequested -= HandleSpawnRequest;
            _hud.OnRemoveRequested -= HandleRemoveRequest;
            _hud.OnGenUIActionTriggered -= HandleGenUIAction;
        }
        foreach (var sprite in _sprites.Values)
            sprite.AgentClicked -= OnAgentClicked;
        _agentWorld?.Dispose();
        _services?.Dispose();
        _logger?.LogInformation("Giant Isopod shutdown");
    }
}
