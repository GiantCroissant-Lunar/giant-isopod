using Godot;
using GiantIsopod.Contracts.Core;

namespace GiantIsopod.Hosts.CompleteApp;

/// <summary>
/// HUD orchestrator — loads generated .tscn scenes, wires events,
/// delegates to TerminalManager and AgentListManager.
/// </summary>
public partial class HudController : Control
{
    public event Action<string>? OnSpawnRequested;
    public event Action<string>? OnRemoveRequested;
    public event Action<string, string, string, string>? OnGenUIActionTriggered;

    // Scene paths
    private const string SwarmHudScene = "res://Scenes/Hud/SwarmhudView.tscn";
    private const string TopBarScene = "res://Scenes/Hud/SwarmtopbarView.tscn";
    private const string AgentPanelScene = "res://Scenes/Hud/AgentlistpanelView.tscn";
    private const string SpawnControlsScene = "res://Scenes/Hud/SpawncontrolsView.tscn";
    private const string ConsoleScene = "res://Scenes/Hud/ConsolepanelView.tscn";

    private HudSceneLoader _sceneLoader = null!;
    private AgentListManager _agentListManager = null!;
    private TerminalManager _terminalManager = null!;

    private OptionButton? _runtimeDropdown;
    private Button? _createButton;
    private List<string> _providerIds = new();
    private Control? _genUIHost;

    // Console state
    private PanelContainer? _consolePanel;
    private Label? _consoleTitle;
    private string? _selectedAgentId;
    private bool _showingTerminal = true;
    private Button? _tabTerminalBtn;
    private Button? _tabRenderedBtn;
    private Control? _terminalContainer;
    private Control? _markdownContainer;

    public override void _Ready()
    {
        _sceneLoader = new HudSceneLoader(this);
        _agentListManager = new AgentListManager();
        _terminalManager = new TerminalManager();

        _agentListManager.OnAgentRemoveClicked += id => OnRemoveRequested?.Invoke(id);

        LoadScenes();
        WireEvents();
    }

    private void LoadScenes()
    {
        // Load the root HUD layout
        var hudRoot = _sceneLoader.LoadScene(SwarmHudScene, this);

        // Load sub-scenes into their slots
        var topBarSlot = hudRoot.GetNode("TopBarSlot");
        var spawnSlot = hudRoot.GetNode("MiddleRow/LeftColumn/SpawnControlsSlot");
        var agentPanelSlot = hudRoot.GetNode("MiddleRow/RightColumn/AgentPanelSlot");
        var consolePanelSlot = hudRoot.GetNode("ConsolePanelSlot");

        _sceneLoader.LoadScene(TopBarScene, topBarSlot);
        _sceneLoader.LoadScene(SpawnControlsScene, spawnSlot);
        _sceneLoader.LoadScene(AgentPanelScene, agentPanelSlot);
        _sceneLoader.LoadScene(ConsoleScene, consolePanelSlot);

        // Grab references from loaded scenes
        var agentCountLabel = _sceneLoader.FindNode<Label>(TopBarScene, "AgentCount")!;
        var runtimeCountLabel = _sceneLoader.FindNode<Label>(TopBarScene, "RuntimeCount")!;
        var versionLabel = _sceneLoader.FindNode<Label>(TopBarScene, "VersionLabel");

        _runtimeDropdown = _sceneLoader.FindNode<OptionButton>(SpawnControlsScene, "RuntimeDropdown");
        _createButton = _sceneLoader.FindNode<Button>(SpawnControlsScene, "CreateButton");

        var agentList = _sceneLoader.FindNode<VBoxContainer>(AgentPanelScene, "VBox/ScrollArea/AgentList")!;

        _consoleTitle = _sceneLoader.FindNode<Label>(ConsoleScene, "VBox/ConsoleHeader/ConsoleTitle");
        _tabTerminalBtn = _sceneLoader.FindNode<Button>(ConsoleScene, "VBox/ConsoleHeader/TerminalTabBtn");
        _tabRenderedBtn = _sceneLoader.FindNode<Button>(ConsoleScene, "VBox/ConsoleHeader/RenderedTabBtn");
        var closeBtn = _sceneLoader.FindNode<Button>(ConsoleScene, "VBox/ConsoleHeader/CloseBtn");
        _terminalContainer = _sceneLoader.FindNode<Control>(ConsoleScene, "VBox/ConsoleBody/TerminalContainer");
        _markdownContainer = _sceneLoader.FindNode<Control>(ConsoleScene, "VBox/ConsoleBody/RenderedContainer");

        _genUIHost = hudRoot.GetNodeOrNull<Control>("MiddleRow/CenterArea/GenUIHost");

        // Console starts hidden
        var consoleInstance = _sceneLoader.GetInstance(ConsoleScene);
        if (consoleInstance != null)
        {
            _consolePanel = consoleInstance as PanelContainer ?? consoleInstance.GetParentOrNull<PanelContainer>();
            consoleInstance.Visible = false;
        }

        // Set version label
        var version = ProjectSettings.GetSetting("application/config/version", "dev").AsString();
        if (versionLabel != null) versionLabel.Text = $"v{version}";

        // Initialize managers
        _agentListManager.Initialize(agentList, agentCountLabel, runtimeCountLabel);
        _terminalManager.Initialize(_terminalContainer!, _markdownContainer!);

        // Ensure dropdown has placeholder
        _runtimeDropdown?.AddItem("(loading...)");
    }

    private void WireEvents()
    {
        _createButton?.Connect("pressed", Callable.From(() =>
        {
            var selectedId = GetSelectedProviderId();
            OnSpawnRequested?.Invoke(selectedId);
        }));

        _tabTerminalBtn?.Connect("pressed", Callable.From(() => SwitchTab(terminal: true)));
        _tabRenderedBtn?.Connect("pressed", Callable.From(() => SwitchTab(terminal: false)));

        var closeBtn = _sceneLoader.FindNode<Button>(ConsoleScene, "VBox/ConsoleHeader/CloseBtn");
        closeBtn?.Connect("pressed", Callable.From(() =>
        {
            var consoleInstance = _sceneLoader.GetInstance(ConsoleScene);
            if (consoleInstance != null) consoleInstance.Visible = false;
            _selectedAgentId = null;
        }));
    }

    /// <summary>
    /// Re-wires events after a hot-reload. Called by HudHotReload.
    /// </summary>
    public void RewireAfterReload(Dictionary<string, Control> newInstances)
    {
        // Re-grab references from reloaded scenes
        LoadScenes();
        WireEvents();
    }

    public void ApplyEvent(ViewportEvent evt)
    {
        switch (evt)
        {
            case AgentSpawnedEvent spawned:
                _agentListManager.AddAgent(spawned.AgentId, spawned.VisualInfo);
                break;
            case AgentDespawnedEvent despawned:
                _agentListManager.RemoveAgent(despawned.AgentId);
                _terminalManager.CleanupAgent(despawned.AgentId);
                break;
            case StateChangedEvent stateChanged:
                _agentListManager.UpdateAgentState(stateChanged.AgentId, stateChanged.State);
                break;
            case GenUIRequestEvent genui:
                ForwardGenUI(genui.AgentId, genui.A2UIJson);
                break;
            case RuntimeStartedEvent started:
                _agentListManager.SetRuntimeConnected(started.AgentId, true);
                break;
            case RuntimeExitedEvent exited:
                _agentListManager.SetRuntimeConnected(exited.AgentId, false);
                break;
            case RuntimeOutputEvent output:
                _terminalManager.AppendOutput(output.AgentId, output.Line, _showingTerminal, _selectedAgentId);
                break;
            case AgUiViewportEvent agui:
                HandleAgUiEvent(agui.AgentId, agui.Event);
                break;
        }
    }

    private void HandleAgUiEvent(string agentId, object evt)
    {
        // Log tool call events to console for visibility
        var evtType = evt.GetType().Name;
        switch (evtType)
        {
            case "ToolCallStartEvent":
                var toolName = evt.GetType().GetProperty("ToolName")?.GetValue(evt)?.ToString() ?? "?";
                _terminalManager.AppendOutput(agentId, $"[AG-UI] Tool call: {toolName}", _showingTerminal, _selectedAgentId);
                break;
            case "TextMessageContentEvent":
                var delta = evt.GetType().GetProperty("Delta")?.GetValue(evt)?.ToString() ?? "";
                if (!string.IsNullOrEmpty(delta))
                    _terminalManager.AppendOutput(agentId, delta, _showingTerminal, _selectedAgentId);
                break;
            case "RunStartedEvent":
                _terminalManager.AppendOutput(agentId, "[AG-UI] Run started", _showingTerminal, _selectedAgentId);
                break;
            case "RunFinishedEvent":
                _terminalManager.AppendOutput(agentId, "[AG-UI] Run finished", _showingTerminal, _selectedAgentId);
                break;
        }
    }

    public void SelectAgent(string agentId)
    {
        _selectedAgentId = agentId;
        if (_consoleTitle != null)
            _consoleTitle.Text = $"Console — {agentId}";

        var consoleInstance = _sceneLoader.GetInstance(ConsoleScene);
        if (consoleInstance != null) consoleInstance.Visible = true;

        _terminalManager.ShowAgent(agentId);

        if (_terminalContainer != null) _terminalContainer.Visible = _showingTerminal;
        if (_markdownContainer != null) _markdownContainer.Visible = !_showingTerminal;

        if (!_showingTerminal)
            _terminalManager.RefreshMarkdown(agentId);
    }

    public void AppendConsoleOutput(string agentId, string line)
    {
        _terminalManager.AppendOutput(agentId, line, _showingTerminal, _selectedAgentId);
    }

    public void SetRuntimes(IReadOnlyCollection<GiantIsopod.Contracts.Protocol.Runtime.RuntimeConfig> runtimes)
    {
        _providerIds.Clear();
        if (_runtimeDropdown == null) return;
        _runtimeDropdown.Clear();
        foreach (var r in runtimes)
        {
            _providerIds.Add(r.Id);
            _runtimeDropdown.AddItem(r.DisplayName ?? r.Id);
        }
        if (_runtimeDropdown.ItemCount > 0)
            _runtimeDropdown.Selected = 0;
    }

    private void SwitchTab(bool terminal)
    {
        _showingTerminal = terminal;
        if (_terminalContainer != null) _terminalContainer.Visible = terminal;
        if (_markdownContainer != null) _markdownContainer.Visible = !terminal;
        UpdateTabStyles();

        if (!terminal && _selectedAgentId != null)
            _terminalManager.RefreshMarkdown(_selectedAgentId);
    }

    private void UpdateTabStyles()
    {
        if (_tabTerminalBtn == null || _tabRenderedBtn == null) return;
        _tabTerminalBtn.ButtonPressed = _showingTerminal;
        _tabRenderedBtn.ButtonPressed = !_showingTerminal;
    }

    private void ForwardGenUI(string agentId, string a2uiJson)
    {
        if (_genUIHost?.HasMethod("render_a2ui") == true)
        {
            // Connect action_triggered signal if not already connected
            if (!_genUIHost.IsConnected("action_triggered", Callable.From<string, string, string, string>(HandleGenUIAction)))
            {
                _genUIHost.Connect("action_triggered",
                    Callable.From<string, string, string, string>(HandleGenUIAction));
            }
            _genUIHost.Call("render_a2ui", agentId, a2uiJson);
        }
    }

    private void HandleGenUIAction(string agentId, string surfaceId, string actionId, string componentId)
    {
        GD.Print($"[GenUI] Action: agent={agentId} surface={surfaceId} action={actionId} component={componentId}");
        OnGenUIActionTriggered?.Invoke(agentId, surfaceId, actionId, componentId);
    }

    private string GetSelectedProviderId()
    {
        if (_runtimeDropdown == null || _providerIds.Count == 0)
            return "pi";
        var idx = _runtimeDropdown.Selected;
        return idx >= 0 && idx < _providerIds.Count ? _providerIds[idx] : _providerIds[0];
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
