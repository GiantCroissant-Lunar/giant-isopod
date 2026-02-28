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

        ApplyTheme();
    }

    private void ApplyTheme()
    {
        // Style the top bar
        var topBar = GetNodeOrNull<HBoxContainer>("TopBar");
        if (topBar != null)
        {
            var topBg = new StyleBoxFlat();
            topBg.BgColor = new Color(0.1f, 0.11f, 0.15f, 0.9f);
            topBg.ContentMarginLeft = 12;
            topBg.ContentMarginRight = 12;
            topBg.ContentMarginTop = 6;
            topBg.ContentMarginBottom = 6;
            topBg.BorderWidthBottom = 1;
            topBg.BorderColor = new Color(0.2f, 0.22f, 0.28f);
            topBar.AddThemeStyleboxOverride("panel", topBg);
        }

        // Style the agent panel
        var agentPanel = GetNodeOrNull<PanelContainer>("AgentPanel");
        if (agentPanel != null)
        {
            var panelBg = new StyleBoxFlat();
            panelBg.BgColor = new Color(0.1f, 0.11f, 0.15f, 0.85f);
            panelBg.ContentMarginLeft = 10;
            panelBg.ContentMarginRight = 10;
            panelBg.ContentMarginTop = 10;
            panelBg.ContentMarginBottom = 10;
            panelBg.CornerRadiusTopLeft = 6;
            panelBg.CornerRadiusBottomLeft = 6;
            panelBg.BorderWidthLeft = 1;
            panelBg.BorderColor = new Color(0.2f, 0.22f, 0.28f);
            agentPanel.AddThemeStyleboxOverride("panel", panelBg);
        }

        // Style labels
        _agentCountLabel?.AddThemeColorOverride("font_color", new Color(0.7f, 0.75f, 0.85f));
        _versionLabel?.AddThemeColorOverride("font_color", new Color(0.45f, 0.48f, 0.55f));
        _versionLabel?.AddThemeFontSizeOverride("font_size", 12);
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

        // Add header on first agent
        if (_entries.Count == 0)
        {
            var header = new Label { Text = "Active Agents" };
            header.AddThemeColorOverride("font_color", new Color(0.55f, 0.58f, 0.65f));
            header.AddThemeFontSizeOverride("font_size", 12);
            _agentList.AddChild(header);

            var sep = new HSeparator();
            sep.AddThemeConstantOverride("separation", 6);
            _agentList.AddChild(sep);
        }

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
        Root.AddThemeConstantOverride("separation", 8);

        // Colored dot indicator
        var dot = new ColorRect();
        dot.CustomMinimumSize = new Vector2(8, 8);
        dot.Color = new Color(0.4f, 0.45f, 0.5f);
        dot.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        Root.AddChild(dot);

        _nameLabel = new Label { Text = displayName };
        _nameLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _nameLabel.AddThemeColorOverride("font_color", new Color(0.8f, 0.82f, 0.88f));
        _nameLabel.AddThemeFontSizeOverride("font_size", 13);

        _stateLabel = new Label { Text = "idle" };
        _stateLabel.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.55f));
        _stateLabel.AddThemeFontSizeOverride("font_size", 11);

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
