using Godot;
using GiantIsopod.Contracts.Core;

namespace GiantIsopod.Hosts.CompleteApp;

/// <summary>
/// HUD controller with agent list, spawn/remove buttons, and status display.
/// </summary>
public partial class HudController : Control
{
    public event Action? OnSpawnRequested;
    public event Action<string>? OnRemoveRequested;

    private Label? _agentCountLabel;
    private Label? _versionLabel;
    private Label? _processCountLabel;
    private VBoxContainer? _agentList;
    private Control? _genUIHost;

    private readonly Dictionary<string, AgentHudEntry> _entries = new();
    private readonly HashSet<string> _activeProcesses = new();

    // Console view
    private PanelContainer? _consolePanel;
    private RichTextLabel? _consoleOutput;
    private Label? _consoleTitle;
    private string? _selectedAgentId;
    private readonly Dictionary<string, List<string>> _consoleBuffers = new();
    private const int MaxConsoleLines = 200;

    public override void _Ready()
    {
        _agentCountLabel = GetNode<Label>("%AgentCount");
        _versionLabel = GetNode<Label>("%VersionLabel");
        _agentList = GetNode<VBoxContainer>("%AgentList");
        _genUIHost = GetNode<Control>("%GenUIHost");

        var topBar = GetNodeOrNull<HBoxContainer>("TopBar");
        if (topBar != null && _agentCountLabel != null)
        {
            _processCountLabel = new Label { Text = "CLI: 0" };
            _processCountLabel.AddThemeColorOverride("font_color", new Color(0.5f, 0.55f, 0.65f));
            topBar.AddChild(_processCountLabel);
            topBar.MoveChild(_processCountLabel, _agentCountLabel.GetIndex() + 1);
        }

        var version = ProjectSettings.GetSetting("application/config/version", "dev").AsString();
        if (_versionLabel != null) _versionLabel.Text = $"v{version}";

        ApplyTheme();
        CreateButtons();
        CreateConsolePanel();
    }

    private void CreateButtons()
    {
        // Floating create button — added directly to this Control (HUDRoot)
        // Positioned top-left, big and obvious
        var spawnBtn = new Button { Text = "+ Create Agent" };
        spawnBtn.Name = "SpawnButton";
        spawnBtn.AddThemeColorOverride("font_color", new Color(0.85f, 1.0f, 0.85f));
        spawnBtn.AddThemeFontSizeOverride("font_size", 16);
        var spawnStyle = new StyleBoxFlat();
        spawnStyle.BgColor = new Color(0.1f, 0.35f, 0.15f, 0.95f);
        spawnStyle.CornerRadiusTopLeft = 6;
        spawnStyle.CornerRadiusTopRight = 6;
        spawnStyle.CornerRadiusBottomLeft = 6;
        spawnStyle.CornerRadiusBottomRight = 6;
        spawnStyle.ContentMarginLeft = 16;
        spawnStyle.ContentMarginRight = 16;
        spawnStyle.ContentMarginTop = 8;
        spawnStyle.ContentMarginBottom = 8;
        spawnStyle.BorderWidthTop = 2;
        spawnStyle.BorderWidthBottom = 2;
        spawnStyle.BorderWidthLeft = 2;
        spawnStyle.BorderWidthRight = 2;
        spawnStyle.BorderColor = new Color(0.3f, 0.7f, 0.35f, 0.8f);
        spawnBtn.AddThemeStyleboxOverride("normal", spawnStyle);
        var spawnHover = (StyleBoxFlat)spawnStyle.Duplicate();
        spawnHover.BgColor = new Color(0.15f, 0.45f, 0.2f, 0.98f);
        spawnBtn.AddThemeStyleboxOverride("hover", spawnHover);
        spawnBtn.Position = new Vector2(12, 40);
        spawnBtn.Pressed += () => OnSpawnRequested?.Invoke();
        AddChild(spawnBtn);

        if (_agentList == null) return;

        // Header in side panel
        var header = new Label { Text = "Agents" };
        header.AddThemeColorOverride("font_color", new Color(0.55f, 0.58f, 0.65f));
        header.AddThemeFontSizeOverride("font_size", 12);
        _agentList.AddChild(header);

        var sep = new HSeparator();
        sep.AddThemeConstantOverride("separation", 6);
        _agentList.AddChild(sep);
    }

    private void ApplyTheme()
    {
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

        _agentCountLabel?.AddThemeColorOverride("font_color", new Color(0.7f, 0.75f, 0.85f));
        _versionLabel?.AddThemeColorOverride("font_color", new Color(0.45f, 0.48f, 0.55f));
        _versionLabel?.AddThemeFontSizeOverride("font_size", 12);
    }

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
            case ProcessStartedEvent started:
                _activeProcesses.Add(started.AgentId);
                UpdateProcessCount();
                if (_entries.TryGetValue(started.AgentId, out var se))
                    se.SetConnected(true);
                break;
            case ProcessExitedEvent exited:
                _activeProcesses.Remove(exited.AgentId);
                UpdateProcessCount();
                if (_entries.TryGetValue(exited.AgentId, out var ee))
                    ee.SetConnected(false);
                break;

            case ProcessOutputEvent output:
                AppendConsoleOutput(output.AgentId, output.Line);
                break;
        }
    }

    private void AddAgent(string agentId, AgentVisualInfo info)
    {
        if (_entries.ContainsKey(agentId) || _agentList == null) return;

        var entry = new AgentHudEntry(agentId, info.DisplayName);
        entry.OnRemoveClicked += () => OnRemoveRequested?.Invoke(agentId);
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
            entry.SetState(state);
    }

    private void ForwardGenUI(string agentId, string a2uiJson)
    {
        if (_genUIHost?.HasMethod("render_a2ui") == true)
            _genUIHost.Call("render_a2ui", agentId, a2uiJson);
    }

    private void UpdateCount()
    {
        if (_agentCountLabel != null)
            _agentCountLabel.Text = $"Agents: {_entries.Count}";
    }

    private void UpdateProcessCount()
    {
        if (_processCountLabel != null)
            _processCountLabel.Text = $"CLI: {_activeProcesses.Count}";
    }

    private void CreateConsolePanel()
    {
        _consolePanel = new PanelContainer();
        _consolePanel.Visible = false;
        _consolePanel.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        _consolePanel.OffsetTop = -220;
        _consolePanel.OffsetBottom = 0;
        _consolePanel.OffsetLeft = 0;
        _consolePanel.OffsetRight = 0;

        var bg = new StyleBoxFlat();
        bg.BgColor = new Color(0.06f, 0.07f, 0.1f, 0.95f);
        bg.BorderWidthTop = 1;
        bg.BorderColor = new Color(0.2f, 0.22f, 0.28f);
        bg.ContentMarginLeft = 10;
        bg.ContentMarginRight = 10;
        bg.ContentMarginTop = 6;
        bg.ContentMarginBottom = 6;
        _consolePanel.AddThemeStyleboxOverride("panel", bg);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 4);
        _consolePanel.AddChild(vbox);

        // Header row with title and close button
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(header);

        _consoleTitle = new Label { Text = "Console" };
        _consoleTitle.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _consoleTitle.AddThemeColorOverride("font_color", new Color(0.7f, 0.75f, 0.85f));
        _consoleTitle.AddThemeFontSizeOverride("font_size", 13);
        header.AddChild(_consoleTitle);

        var closeBtn = new Button { Text = "✕" };
        closeBtn.AddThemeFontSizeOverride("font_size", 11);
        closeBtn.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.65f));
        var closeBg = new StyleBoxFlat();
        closeBg.BgColor = new Color(0.15f, 0.15f, 0.2f, 0.6f);
        closeBg.CornerRadiusTopLeft = 3;
        closeBg.CornerRadiusTopRight = 3;
        closeBg.CornerRadiusBottomLeft = 3;
        closeBg.CornerRadiusBottomRight = 3;
        closeBg.ContentMarginLeft = 4;
        closeBg.ContentMarginRight = 4;
        closeBg.ContentMarginTop = 2;
        closeBg.ContentMarginBottom = 2;
        closeBtn.AddThemeStyleboxOverride("normal", closeBg);
        closeBtn.Pressed += () => { _consolePanel.Visible = false; _selectedAgentId = null; };
        header.AddChild(closeBtn);

        // Output area
        _consoleOutput = new RichTextLabel();
        _consoleOutput.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _consoleOutput.BbcodeEnabled = true;
        _consoleOutput.ScrollFollowing = true;
        _consoleOutput.AddThemeColorOverride("default_color", new Color(0.75f, 0.8f, 0.7f));
        _consoleOutput.AddThemeFontSizeOverride("normal_font_size", 12);
        vbox.AddChild(_consoleOutput);

        AddChild(_consolePanel);
    }

    public void SelectAgent(string agentId)
    {
        _selectedAgentId = agentId;
        if (_consolePanel == null || _consoleOutput == null || _consoleTitle == null) return;

        var displayName = _entries.TryGetValue(agentId, out var entry) ? agentId : agentId;
        _consoleTitle.Text = $"Console — {agentId}";
        _consolePanel.Visible = true;

        // Render buffered lines
        _consoleOutput.Clear();
        if (_consoleBuffers.TryGetValue(agentId, out var lines))
        {
            foreach (var line in lines)
                _consoleOutput.AppendText(line + "\n");
        }
        else
        {
            _consoleOutput.AppendText("[color=#666]No output yet[/color]\n");
        }
    }

    public void AppendConsoleOutput(string agentId, string line)
    {
        if (!_consoleBuffers.TryGetValue(agentId, out var buffer))
        {
            buffer = new List<string>();
            _consoleBuffers[agentId] = buffer;
        }

        buffer.Add(line);
        if (buffer.Count > MaxConsoleLines)
            buffer.RemoveAt(0);

        // If this agent's console is currently visible, append live
        if (_selectedAgentId == agentId && _consoleOutput != null)
        {
            _consoleOutput.AppendText(line + "\n");
        }
    }
}

/// <summary>
/// HUD entry for a single agent with status dot, name, state, and remove button.
/// </summary>
public sealed class AgentHudEntry
{
    public HBoxContainer Root { get; }
    public event Action? OnRemoveClicked;

    private readonly Label _stateLabel;
    private readonly ColorRect _dot;

    public AgentHudEntry(string agentId, string displayName)
    {
        Root = new HBoxContainer();
        Root.AddThemeConstantOverride("separation", 6);

        _dot = new ColorRect();
        _dot.CustomMinimumSize = new Vector2(8, 8);
        _dot.Color = new Color(0.4f, 0.45f, 0.5f);
        _dot.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        Root.AddChild(_dot);

        var nameLabel = new Label { Text = displayName };
        nameLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        nameLabel.AddThemeColorOverride("font_color", new Color(0.8f, 0.82f, 0.88f));
        nameLabel.AddThemeFontSizeOverride("font_size", 13);
        Root.AddChild(nameLabel);

        _stateLabel = new Label { Text = "idle" };
        _stateLabel.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.55f));
        _stateLabel.AddThemeFontSizeOverride("font_size", 11);
        Root.AddChild(_stateLabel);

        var removeBtn = new Button { Text = "✕" };
        removeBtn.AddThemeFontSizeOverride("font_size", 11);
        removeBtn.AddThemeColorOverride("font_color", new Color(0.7f, 0.3f, 0.3f));
        var btnStyle = new StyleBoxFlat();
        btnStyle.BgColor = new Color(0.2f, 0.12f, 0.12f, 0.6f);
        btnStyle.CornerRadiusTopLeft = 3;
        btnStyle.CornerRadiusTopRight = 3;
        btnStyle.CornerRadiusBottomLeft = 3;
        btnStyle.CornerRadiusBottomRight = 3;
        btnStyle.ContentMarginLeft = 4;
        btnStyle.ContentMarginRight = 4;
        btnStyle.ContentMarginTop = 2;
        btnStyle.ContentMarginBottom = 2;
        removeBtn.AddThemeStyleboxOverride("normal", btnStyle);
        var hoverStyle = (StyleBoxFlat)btnStyle.Duplicate();
        hoverStyle.BgColor = new Color(0.35f, 0.15f, 0.15f, 0.8f);
        removeBtn.AddThemeStyleboxOverride("hover", hoverStyle);
        removeBtn.Pressed += () => OnRemoveClicked?.Invoke();
        Root.AddChild(removeBtn);
    }

    public void SetConnected(bool connected)
    {
        _dot.Color = connected
            ? new Color(0.2f, 0.8f, 0.4f)
            : new Color(0.4f, 0.45f, 0.5f);
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
            _ => "?"
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
