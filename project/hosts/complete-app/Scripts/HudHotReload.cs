using Godot;

namespace GiantIsopod.Hosts.CompleteApp;

/// <summary>
/// Debug-only HUD hot-reload support. Watches Scenes/Hud/*.tscn for changes
/// and triggers HudSceneLoader.ReloadAll() + re-wiring.
/// </summary>
public sealed class HudHotReload
{
    private readonly HudController _hud;

    public HudHotReload(HudController hud)
    {
        _hud = hud;
    }

    /// <summary>
    /// Manually trigger a full HUD reload (bound to F6).
    /// Frees old scene instances, re-loads .tscn from disk, re-wires events.
    /// </summary>
    public void ReloadHud()
    {
        GD.Print("[HudHotReload] Reloading HUD scenes...");

        // Remove all children of HUDRoot (the loaded scenes)
        foreach (var child in _hud.GetChildren())
        {
            if (child is Control)
            {
                child.QueueFree();
            }
        }

        // Force Godot to re-read scene files from disk on next frame
        Callable.From(() =>
        {
            // Re-trigger _Ready which re-loads all scenes
            _hud._Ready();
            GD.Print("[HudHotReload] HUD reload complete.");
        }).CallDeferred();
    }
}
