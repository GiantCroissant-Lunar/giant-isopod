using Godot;

namespace GiantIsopod.Hosts.CompleteApp;

/// <summary>
/// Loads .tscn HUD scenes at runtime via ResourceLoader and manages hot-reload.
/// </summary>
public sealed class HudSceneLoader
{
    private readonly Control _parent;
    private readonly List<LoadedScene> _loaded = new();

    public HudSceneLoader(Control parent)
    {
        _parent = parent;
    }

    /// <summary>
    /// Loads a .tscn scene and adds it as a child of the specified slot node.
    /// Returns the instantiated Control root.
    /// </summary>
    public Control LoadScene(string resPath, Node slot)
    {
        var packed = ResourceLoader.Load<PackedScene>(resPath);
        if (packed == null)
            throw new System.InvalidOperationException($"Failed to load scene: {resPath}");

        var instance = packed.Instantiate<Control>();
        instance.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        instance.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        slot.AddChild(instance);

        _loaded.Add(new LoadedScene(resPath, slot, instance));
        return instance;
    }

    /// <summary>
    /// Reloads all loaded scenes, preserving the slot structure.
    /// Returns the new root instances keyed by resource path.
    /// </summary>
    public Dictionary<string, Control> ReloadAll()
    {
        var result = new Dictionary<string, Control>();

        for (int i = 0; i < _loaded.Count; i++)
        {
            var entry = _loaded[i];
            entry.Instance.QueueFree();

            // Force re-read from disk by invalidating cache
            ResourceLoader.Load<PackedScene>(entry.ResPath, cacheMode: ResourceLoader.CacheMode.Ignore);
            var packed = ResourceLoader.Load<PackedScene>(entry.ResPath);
            if (packed == null) continue;

            var newInstance = packed.Instantiate<Control>();
            newInstance.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            newInstance.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
            entry.Slot.AddChild(newInstance);

            _loaded[i] = new LoadedScene(entry.ResPath, entry.Slot, newInstance);
            result[entry.ResPath] = newInstance;
        }

        return result;
    }

    /// <summary>Returns the most recently loaded instance for a given resource path.</summary>
    public Control? GetInstance(string resPath)
    {
        for (int i = _loaded.Count - 1; i >= 0; i--)
        {
            if (_loaded[i].ResPath == resPath)
                return _loaded[i].Instance;
        }
        return null;
    }

    /// <summary>Finds a node by path within a loaded scene instance.</summary>
    public T? FindNode<T>(string scenePath, string nodePath) where T : Node
    {
        var instance = GetInstance(scenePath);
        return instance?.GetNodeOrNull<T>(nodePath);
    }

    private record struct LoadedScene(string ResPath, Node Slot, Control Instance);
}
