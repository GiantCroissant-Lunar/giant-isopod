using Godot;
using GiantIsopod.Plugin.Actors;

namespace GiantIsopod.Hosts.CompleteApp;

/// <summary>
/// Main scene entry point. Bootstraps the Akka.NET actor system
/// and connects it to the Godot viewport via the ECS bridge.
/// </summary>
public partial class Main : Node2D
{
    private AgentWorldSystem? _agentWorld;
    private GodotViewportBridge? _viewportBridge;
    private HudController? _hud;

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

        _agentWorld = new AgentWorldSystem(config);
        _viewportBridge = new GodotViewportBridge();

        // Wire the viewport bridge into the actor system
        _agentWorld.SetViewportBridge(_viewportBridge);

        // Grab HUD controller from scene tree
        _hud = GetNode<HudController>("HUD/HUDRoot");

        GD.Print("[AgentWorld] Actor system started");
    }

    public override void _Process(double delta)
    {
        if (_viewportBridge == null || _hud == null) return;

        // Drain actor system events and apply to HUD + ECS
        foreach (var evt in _viewportBridge.DrainEvents())
        {
            _hud.ApplyEvent(evt);
        }

        // ECS systems would tick here
        // ViewportSyncSystem drains the actor message queue
        // MovementSystem updates positions
        // AnimationSystem updates sprite frames
    }

    public override void _ExitTree()
    {
        _agentWorld?.Dispose();
        GD.Print("[AgentWorld] Actor system stopped");
    }
}
