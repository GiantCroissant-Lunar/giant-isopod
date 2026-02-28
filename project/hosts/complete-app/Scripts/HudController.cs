using Godot;
using GiantIsopod.Contracts.Core;

namespace GiantIsopod.Hosts.CompleteApp;

/// <summary>
/// Thin glue between viewport bridge events and HUD Control nodes.
/// Updates agent count, agent list panel, and version label.
/// GenUI rendering is delegated to PCK-loaded GDScript.
/// </summary>
public partial class HudController : Control
{
    private Label? _agentCountLabel;
    private Label? _versionLabel;
    private VBoxContainer? _agentList;
    private Control? _genUIHost;

    private readonly Dictionary<string, AgentHudEntry> _entries = new();

    public override void _Ready()
    {
        _agentCountLabel = GetNode<Label>("%AgentCount");
        _versionLabel = GetNode<Label>("%VersionLabel");
        _agentList = GetNode<VBoxContainer>("%AgentList");
        _genUIHost = GetNode<Control>("%GenUIHost");

        // Set version from assembly info or project setting
        var version = ProjectSettings.GetSetting("application/config/version", "dev").AsString();
        if (_versionLabel != null) _versionLabel.Text = $"v{version}";
    }

    /// <summary>
    /// Called from Main._Process to apply viewport events to HUD.
    /// </summary>
    public void ApplyEvent(ViewportEvent evt)
    {
        switch (evt)
        {
            case AgentSpawnedEvent spawned:
                AddAgent(spawned.AgentId, spawned.VisualInfo);
                break;

            case AgentDespawnedEvent despawned:
                RemoveAgent(despawned.AgentId);
                break;

            case StateChangedEvent stateChanged:
                UpdateAgentState(stateChanged.AgentId, stateChanged.State);
                break;

            case GenUIRequestEvent genui:
                ForwardGenUI(genui.AgentId, genui.A2UIJson);
                break;
        }
    }

    private void AddAgent(string agentId, AgentVisualInfo info)
    {
        if (_entries.ContainsKey(agentId) || _agentList == null) return;

        var entry = new AgentHudEntry(agentId, info.DisplayName);
        _agentList.AddChild(entry.Root);
        _entries[agentId] = entry;
        UpdateCount();
    }

    private void RemoveAgent(string agentId)
    {
        if (!_entries.TryGetValue(agentId, out var entry)) return;

        entry.Root.QueueFree();
        _entries.Remove(agentId);
        UpdateCount();
    }

    private void UpdateAgentState(string agentId, AgentActivityState state)
    {
        if (_entries.TryGetValue(agentId, out var entry))
        {
            entry.SetState(state);
        }
    }

    private void ForwardGenUI(string agentId, string a2uiJson)
    {
        // Delegate to PCK-loaded GDScript via signal or method call
        // The GenUIHost node will have a GDScript from the PCK that handles rendering
        if (_genUIHost?.HasMethod("render_a2ui") == true)
        {
            _genUIHost.Call("render_a2ui", agentId, a2uiJson);
        }
    }

    private void UpdateCount()
    {
        if (_agentCountLabel != null)
            _agentCountLabel.Text = $"Agents: {_entries.Count}";
    }
}

/// <summary>
/// Lightweight HUD entry for a single agent in the side panel.
/// </summary>
public sealed class AgentHudEntry
{
    public HBoxContainer Root { get; }
    private readonly Label _nameLabel;
    private readonly Label _stateLabel;

    public AgentHudEntry(string agentId, string displayName)
    {
        Root = new HBoxContainer();

        _nameLabel = new Label { Text = displayName };
        _nameLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        _stateLabel = new Label { Text = "idle" };
        _stateLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));

        Root.AddChild(_nameLabel);
        Root.AddChild(_stateLabel);
    }

    public void SetState(AgentActivityState state)
    {
        _stateLabel.Text = state switch
        {
            AgentActivityState.Idle => "idle",
            AgentActivityState.Walking => "walking",
            AgentActivityState.Typing => "typing",
            AgentActivityState.Reading => "reading",
            AgentActivityState.Waiting => "waiting",
            AgentActivityState.Thinking => "thinking",
            _ => "unknown"
        };

        _stateLabel.RemoveThemeColorOverride("font_color");
        _stateLabel.AddThemeColorOverride("font_color", state switch
        {
            AgentActivityState.Typing => new Color(0.3f, 0.8f, 0.3f),
            AgentActivityState.Thinking => new Color(0.8f, 0.7f, 0.2f),
            AgentActivityState.Reading => new Color(0.3f, 0.6f, 0.9f),
            AgentActivityState.Waiting => new Color(0.6f, 0.6f, 0.6f),
            _ => new Color(0.5f, 0.5f, 0.5f)
        });
    }
}
