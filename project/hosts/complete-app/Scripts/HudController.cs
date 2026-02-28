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

    // Console view — GodotXterm Terminal + PTY per agent
    private PanelContainer? _consolePanel;
    private Label? _consoleTitle;
    private string? _selectedAgentId;
    private Control? _terminalContainer; // holds the currently visible Terminal
    private readonly Dictionary<string, GodotObject> _agentTerminals = new(); // agentId → Terminal node
    private readonly Dictionary<string, GodotObject> _agentPtys = new(); // agentId → PTY node
    private static readonly string ConsoleLogPath = System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
        "giant-isopod-console.log");

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
        CleanupTerminalForAgent(agentId);
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
        _consolePanel.AnchorLeft = 0;
        _consolePanel.AnchorRight = 1;
        _consolePanel.AnchorTop = 1;
        _consolePanel.AnchorBottom = 1;
        _consolePanel.OffsetTop = -220;
        _consolePanel.OffsetBottom = 0;
        _consolePanel.OffsetLeft = 0;
        _consolePanel.OffsetRight = 0;
        _consolePanel.GrowHorizontal = Control.GrowDirection.Both;
        _consolePanel.GrowVertical = Control.GrowDirection.Begin;

        var bg = new StyleBoxFlat();
        bg.BgColor = new Color(0.06f, 0.07f, 0.1f, 0.95f);
        bg.BorderWidthTop = 2;
        bg.BorderColor = new Color(0.25f, 0.5f, 0.3f, 0.8f);
        bg.ContentMarginLeft = 4;
        bg.ContentMarginRight = 4;
        bg.ContentMarginTop = 4;
        bg.ContentMarginBottom = 4;
        _consolePanel.AddThemeStyleboxOverride("panel", bg);

        var vbox = new VBoxContainer();
        vbox.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        vbox.AddThemeConstantOverride("separation", 2);
        _consolePanel.AddChild(vbox);

        // Header row
        var header = new HBoxContainer();
        vbox.AddChild(header);

        _consoleTitle = new Label { Text = "Console" };
        _consoleTitle.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _consoleTitle.AddThemeColorOverride("font_color", new Color(0.3f, 0.85f, 0.4f));
        _consoleTitle.AddThemeFontSizeOverride("font_size", 11);
        header.AddChild(_consoleTitle);

        var closeBtn = new Button { Text = "✕" };
        closeBtn.AddThemeFontSizeOverride("font_size", 10);
        closeBtn.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.65f));
        var closeBg = new StyleBoxFlat();
        closeBg.BgColor = new Color(0.15f, 0.15f, 0.2f, 0.6f);
        closeBg.CornerRadiusTopLeft = 3;
        closeBg.CornerRadiusTopRight = 3;
        closeBg.CornerRadiusBottomLeft = 3;
        closeBg.CornerRadiusBottomRight = 3;
        closeBg.ContentMarginLeft = 4;
        closeBg.ContentMarginRight = 4;
        closeBg.ContentMarginTop = 1;
        closeBg.ContentMarginBottom = 1;
        closeBtn.AddThemeStyleboxOverride("normal", closeBg);
        closeBtn.Pressed += () => { _consolePanel.Visible = false; _selectedAgentId = null; };
        header.AddChild(closeBtn);

        // Container for Terminal nodes — one per agent, only the selected one is visible
        _terminalContainer = new Control();
        _terminalContainer.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _terminalContainer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _terminalContainer.CustomMinimumSize = new Vector2(0, 180);
        vbox.AddChild(_terminalContainer);

        AddChild(_consolePanel);
    }

    /// <summary>
    /// Creates a GodotXterm Terminal + PTY pair for an agent and forks pi.
    /// </summary>
    private void CreateTerminalForAgent(string agentId)
    {
        if (_agentTerminals.ContainsKey(agentId) || _terminalContainer == null) return;

        // Create Terminal node (GDExtension class)
        var terminal = ClassDB.Instantiate("Terminal").AsGodotObject();
        if (terminal is not Control termControl)
        {
            GD.PrintErr($"Failed to create Terminal node for {agentId}");
            return;
        }

        termControl.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        termControl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        termControl.SetAnchorsPreset(LayoutPreset.FullRect);
        termControl.Visible = false;
        _terminalContainer.AddChild(termControl);

        // Create PTY node
        var pty = ClassDB.Instantiate("PTY").AsGodotObject();
        if (pty is not Node ptyNode)
        {
            GD.PrintErr($"Failed to create PTY node for {agentId}");
            return;
        }

        // Set PTY terminal_path to point to the Terminal node
        ptyNode.Set("terminal_path", termControl.GetPath());
        // Set environment variables for pi
        var env = new Godot.Collections.Dictionary();
        env["COLORTERM"] = "truecolor";
        env["TERM"] = "xterm-256color";
        env["ZAI_API_KEY"] = "08bbb0b6b8d649fbbafa5c11091e5ac3.4dzlUajBX9I8oE0F";
        ptyNode.Set("env", env);
        ptyNode.Set("use_os_env", true);

        termControl.AddChild(ptyNode);

        _agentTerminals[agentId] = terminal;
        _agentPtys[agentId] = pty;

        // Fork pi in text mode
        var args = new string[] { "--mode", "text", "--no-session", "--provider", "zai", "--model", "glm-4.7",
            "-p", "Explore the current directory, read key files, and suggest improvements." };
        var cwd = @"C:\lunar-horse\yokan-projects\giant-isopod";
        var result = ptyNode.Call("fork", "pi", args, cwd, 120, 24);

        GD.Print($"PTY fork for {agentId}: {result}");
    }

    /// <summary>
    /// Cleans up Terminal + PTY for a removed agent.
    /// </summary>
    private void CleanupTerminalForAgent(string agentId)
    {
        if (_agentPtys.TryGetValue(agentId, out var pty) && pty is Node ptyNode)
        {
            ptyNode.Call("kill", 9); // SIGKILL
            ptyNode.QueueFree();
            _agentPtys.Remove(agentId);
        }
        if (_agentTerminals.TryGetValue(agentId, out var term) && term is Control termControl)
        {
            termControl.QueueFree();
            _agentTerminals.Remove(agentId);
        }
    }

    public void SelectAgent(string agentId)
    {
        _selectedAgentId = agentId;
        if (_consolePanel == null || _consoleTitle == null) return;

        _consoleTitle.Text = $"Console — {agentId}";
        _consolePanel.Visible = true;

        // Create terminal on first click
        if (!_agentTerminals.ContainsKey(agentId))
            CreateTerminalForAgent(agentId);

        // Hide all terminals, show the selected one
        foreach (var (id, term) in _agentTerminals)
        {
            if (term is Control c)
                c.Visible = id == agentId;
        }
    }

    public void AppendConsoleOutput(string agentId, string line)
    {
        // With GodotXterm, console output goes through PTY directly.
        // This method is kept for compatibility but only logs to file now.
        try
        {
            System.IO.File.AppendAllText(ConsoleLogPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] [{agentId}] {line}\n");
        }
        catch { /* ignore file errors */ }
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
