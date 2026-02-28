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
    private readonly Dictionary<string, GodotObject> _agentTerminals = new(); // agentId → AgentTerminal instance
    private readonly Dictionary<string, GiantIsopod.Plugin.Process.AsciicastRecorder> _agentRecorders = new();

    // Markdown rendered view — RichTextLabel per agent
    private Control? _markdownContainer;
    private readonly Dictionary<string, RichTextLabel> _agentMarkdownLabels = new();
    private readonly Dictionary<string, System.Text.StringBuilder> _agentMarkdownBuffers = new();
    private Button? _tabTerminalBtn;
    private Button? _tabRenderedBtn;
    private bool _showingTerminal = true;

    private static readonly string ConsoleLogPath = System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
        "giant-isopod-console.log");
    private static readonly string RecordingsDir = System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
        "giant-isopod-recordings");

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
        _consolePanel.OffsetTop = -350;
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

        // Tab buttons: Terminal | Rendered
        _tabTerminalBtn = CreateTabButton("Terminal", true);
        _tabRenderedBtn = CreateTabButton("Rendered", false);
        _tabTerminalBtn.Pressed += () => SwitchTab(terminal: true);
        _tabRenderedBtn.Pressed += () => SwitchTab(terminal: false);
        header.AddChild(_tabTerminalBtn);
        header.AddChild(_tabRenderedBtn);

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
        _terminalContainer = new MarginContainer();
        _terminalContainer.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _terminalContainer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _terminalContainer.CustomMinimumSize = new Vector2(0, 300);
        vbox.AddChild(_terminalContainer);

        // Container for Markdown rendered views — one RichTextLabel per agent
        _markdownContainer = new MarginContainer();
        _markdownContainer.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _markdownContainer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _markdownContainer.CustomMinimumSize = new Vector2(0, 300);
        _markdownContainer.Visible = false;
        vbox.AddChild(_markdownContainer);

        AddChild(_consolePanel);
    }

    private Button CreateTabButton(string text, bool active)
    {
        var btn = new Button { Text = text };
        btn.AddThemeFontSizeOverride("font_size", 10);
        var style = new StyleBoxFlat();
        style.CornerRadiusTopLeft = 3;
        style.CornerRadiusTopRight = 3;
        style.CornerRadiusBottomLeft = 3;
        style.CornerRadiusBottomRight = 3;
        style.ContentMarginLeft = 8;
        style.ContentMarginRight = 8;
        style.ContentMarginTop = 2;
        style.ContentMarginBottom = 2;
        style.BgColor = active ? new Color(0.2f, 0.4f, 0.25f, 0.8f) : new Color(0.15f, 0.15f, 0.2f, 0.6f);
        btn.AddThemeStyleboxOverride("normal", style);
        btn.AddThemeColorOverride("font_color", active ? new Color(0.85f, 1f, 0.85f) : new Color(0.5f, 0.55f, 0.6f));
        return btn;
    }

    private void UpdateTabStyles()
    {
        if (_tabTerminalBtn == null || _tabRenderedBtn == null) return;
        var activeColor = new Color(0.2f, 0.4f, 0.25f, 0.8f);
        var inactiveColor = new Color(0.15f, 0.15f, 0.2f, 0.6f);

        var termStyle = (StyleBoxFlat)_tabTerminalBtn.GetThemeStylebox("normal").Duplicate();
        termStyle.BgColor = _showingTerminal ? activeColor : inactiveColor;
        _tabTerminalBtn.AddThemeStyleboxOverride("normal", termStyle);
        _tabTerminalBtn.RemoveThemeColorOverride("font_color");
        _tabTerminalBtn.AddThemeColorOverride("font_color", _showingTerminal ? new Color(0.85f, 1f, 0.85f) : new Color(0.5f, 0.55f, 0.6f));

        var rendStyle = (StyleBoxFlat)_tabRenderedBtn.GetThemeStylebox("normal").Duplicate();
        rendStyle.BgColor = !_showingTerminal ? activeColor : inactiveColor;
        _tabRenderedBtn.AddThemeStyleboxOverride("normal", rendStyle);
        _tabRenderedBtn.RemoveThemeColorOverride("font_color");
        _tabRenderedBtn.AddThemeColorOverride("font_color", !_showingTerminal ? new Color(0.85f, 1f, 0.85f) : new Color(0.5f, 0.55f, 0.6f));
    }

    private void SwitchTab(bool terminal)
    {
        _showingTerminal = terminal;
        if (_terminalContainer != null) _terminalContainer.Visible = terminal;
        if (_markdownContainer != null) _markdownContainer.Visible = !terminal;
        UpdateTabStyles();

        // When switching to rendered, refresh the current agent's markdown
        if (!terminal && _selectedAgentId != null)
            RefreshMarkdown(_selectedAgentId);
    }

    private RichTextLabel CreateMarkdownLabelForAgent(string agentId)
    {
        if (_agentMarkdownLabels.TryGetValue(agentId, out var existing))
            return existing;

        var rtl = new RichTextLabel();
        rtl.BbcodeEnabled = true;
        rtl.FitContent = false;
        rtl.ScrollFollowing = true;
        rtl.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        rtl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        rtl.AddThemeColorOverride("default_color", new Color(0.82f, 0.84f, 0.9f));
        rtl.AddThemeFontSizeOverride("normal_font_size", 13);
        rtl.Visible = false;
        _markdownContainer?.AddChild(rtl);
        _agentMarkdownLabels[agentId] = rtl;
        return rtl;
    }

    private void RefreshMarkdown(string agentId)
    {
        if (!_agentMarkdownBuffers.TryGetValue(agentId, out var buffer)) return;
        if (!_agentMarkdownLabels.TryGetValue(agentId, out var label))
            label = CreateMarkdownLabelForAgent(agentId);

        var bbcode = MarkdownBBCode.Convert(buffer.ToString());
        label.Clear();
        label.AppendText(""); // reset
        label.ParseBbcode(bbcode);
    }

    /// <summary>
    /// Creates a GodotXterm Terminal + PTY pair for an agent by instantiating the AgentTerminal scene.
    /// </summary>
    private void CreateTerminalForAgent(string agentId)
    {
        if (_agentTerminals.ContainsKey(agentId) || _terminalContainer == null) return;

        var scene = GD.Load<PackedScene>("res://Scenes/AgentTerminal.tscn");
        if (scene == null)
        {
            GD.PrintErr($"Failed to load AgentTerminal.tscn for {agentId}");
            return;
        }

        var instance = scene.Instantiate<Control>();
        instance.Visible = false;
        instance.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        instance.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _terminalContainer.AddChild(instance);

        _agentTerminals[agentId] = instance;

        // Make visible after _ready() runs on next frame
        Callable.From(() =>
        {
            instance.Visible = true;
            instance.Call("write_text", $"\u001b[32m● Agent {agentId} connected\u001b[0m\r\n");
            DebugLog($"Terminal created for {agentId}: container={_terminalContainer!.Size}, instance={instance.Size}");

            // Start asciicast recording
            try
            {
                var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var castPath = System.IO.Path.Combine(RecordingsDir, $"{agentId}-{timestamp}.cast");
                var recorder = new GiantIsopod.Plugin.Process.AsciicastRecorder(castPath, 120, 24);
                _agentRecorders[agentId] = recorder;
                recorder.WriteOutput($"\u001b[32m● Agent {agentId} connected\u001b[0m\r\n");
                DebugLog($"Recording to {castPath}");
            }
            catch (Exception ex)
            {
                DebugLog($"Failed to start recording for {agentId}: {ex.Message}");
            }
        }).CallDeferred();
    }

    /// <summary>
    /// Cleans up Terminal + PTY for a removed agent.
    /// </summary>
    private void CleanupTerminalForAgent(string agentId)
    {
        if (_agentTerminals.TryGetValue(agentId, out var term) && term is Control termControl)
        {
            termControl.QueueFree();
            _agentTerminals.Remove(agentId);
        }

        // Clean up markdown label
        if (_agentMarkdownLabels.TryGetValue(agentId, out var label))
        {
            label.QueueFree();
            _agentMarkdownLabels.Remove(agentId);
        }
        _agentMarkdownBuffers.Remove(agentId);

        // Stop asciicast recording
        if (_agentRecorders.TryGetValue(agentId, out var recorder))
        {
            _ = recorder.DisposeAsync();
            _agentRecorders.Remove(agentId);
        }
    }

    public void SelectAgent(string agentId)
    {
        _selectedAgentId = agentId;
        if (_consolePanel == null || _consoleTitle == null) return;

        _consoleTitle.Text = $"Console — {agentId}";
        _consolePanel.Visible = true;
        DebugLog($"SelectAgent: {agentId}, panel visible, has terminal: {_agentTerminals.ContainsKey(agentId)}");

        // Create terminal on first click
        if (!_agentTerminals.ContainsKey(agentId))
            CreateTerminalForAgent(agentId);

        // Ensure markdown label exists
        if (!_agentMarkdownLabels.ContainsKey(agentId))
            CreateMarkdownLabelForAgent(agentId);

        // Hide all terminals, show the selected one
        foreach (var (id, term) in _agentTerminals)
        {
            if (term is Control c)
                c.Visible = id == agentId;
        }

        // Hide all markdown labels, show the selected one
        foreach (var (id, label) in _agentMarkdownLabels)
            label.Visible = id == agentId;

        // Respect current tab
        if (_terminalContainer != null) _terminalContainer.Visible = _showingTerminal;
        if (_markdownContainer != null) _markdownContainer.Visible = !_showingTerminal;

        // Refresh markdown for selected agent
        if (!_showingTerminal)
            RefreshMarkdown(agentId);
    }

    public void AppendConsoleOutput(string agentId, string line)
    {
        if (!_agentTerminals.TryGetValue(agentId, out var term)) return;

        // ListenAsync gives lines without newlines, Terminal needs \r\n
        var text = ColorizeOutput(line) + "\r\n";

        term.Call("write_text", text);

        // Record to asciicast
        if (_agentRecorders.TryGetValue(agentId, out var recorder))
            recorder.WriteOutput(text);

        // Accumulate raw text for markdown rendering
        if (!_agentMarkdownBuffers.TryGetValue(agentId, out var buffer))
        {
            buffer = new System.Text.StringBuilder();
            _agentMarkdownBuffers[agentId] = buffer;
        }
        buffer.AppendLine(line);

        // Live-refresh markdown if currently viewing rendered tab for this agent
        if (!_showingTerminal && _selectedAgentId == agentId)
            RefreshMarkdown(agentId);
    }

    /// <summary>
    /// Adds ANSI color codes to pi text output for better readability.
    /// </summary>
    private static string ColorizeOutput(string text)
    {
        // Tool use lines
        if (text.Contains("🔧") || text.Contains("tool_use"))
            return $"\u001b[33m{text}\u001b[0m"; // yellow

        // Thinking lines
        if (text.Contains("💭"))
            return $"\u001b[36m{text}\u001b[0m"; // cyan

        // Headings (markdown ##)
        if (text.TrimStart().StartsWith("##"))
            return $"\u001b[1;32m{text}\u001b[0m"; // bold green

        // Code blocks
        if (text.TrimStart().StartsWith("```"))
            return $"\u001b[90m{text}\u001b[0m"; // dim gray

        // Bullet points
        if (text.TrimStart().StartsWith("- ") || text.TrimStart().StartsWith("* "))
            return $"\u001b[37m{text}\u001b[0m"; // bright white

        // Error/warning
        if (text.Contains("error") || text.Contains("Error"))
            return $"\u001b[31m{text}\u001b[0m"; // red

        return text;
    }

    private void DebugLog(string message)
    {
        try
        {
            System.IO.File.AppendAllText(ConsoleLogPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
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
