using Godot;

namespace GiantIsopod.Hosts.CompleteApp;

/// <summary>
/// Manages per-agent terminal instances (GodotXterm or fallback RichTextLabel),
/// asciicast recording, output buffering, and ANSI colorization.
/// </summary>
public sealed class TerminalManager
{
    private Control? _terminalContainer;
    private Control? _markdownContainer;

    private readonly Dictionary<string, GodotObject> _agentTerminals = new();
    private readonly HashSet<string> _fallbackTerminals = new();
    private readonly Dictionary<string, GiantIsopod.Plugin.Process.AsciicastRecorder> _agentRecorders = new();
    private readonly Dictionary<string, RichTextLabel> _agentMarkdownLabels = new();
    private readonly Dictionary<string, System.Text.StringBuilder> _agentMarkdownBuffers = new();
    private readonly Dictionary<string, List<string>> _pendingOutput = new();

    private string _consoleLogPath = null!;
    private string _recordingsDir = null!;

    public void Initialize(Control terminalContainer, Control markdownContainer)
    {
        _terminalContainer = terminalContainer;
        _markdownContainer = markdownContainer;

        var userDir = ProjectSettings.GlobalizePath("user://");
        _consoleLogPath = System.IO.Path.Combine(userDir, "giant-isopod-console.log");
        _recordingsDir = System.IO.Path.Combine(userDir, "recordings");
    }

    public bool HasTerminal(string agentId) => _agentTerminals.ContainsKey(agentId);

    public void CreateTerminalForAgent(string agentId)
    {
        if (_agentTerminals.ContainsKey(agentId) || _terminalContainer == null) return;

        Control? instance = null;
        bool usingFallback = false;

        try
        {
            var scene = GD.Load<PackedScene>("res://Scenes/AgentTerminal.tscn");
            if (scene != null)
            {
                var candidate = scene.Instantiate<Control>();
                var terminalNode = candidate.GetNodeOrNull("Terminal");
                if (terminalNode != null && terminalNode.HasMethod("write"))
                {
                    instance = candidate;
                }
                else
                {
                    candidate.QueueFree();
                    DebugLog("GodotXterm Terminal node missing write() method — native lib not loaded");
                }
            }
        }
        catch (System.Exception ex)
        {
            DebugLog($"GodotXterm terminal failed for {agentId}: {ex.Message}");
        }

        if (instance == null)
        {
            instance = CreateFallbackTerminal();
            usingFallback = true;
            DebugLog($"Using fallback RichTextLabel terminal for {agentId}");
        }

        instance.Visible = false;
        instance.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        instance.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _terminalContainer.AddChild(instance);
        _agentTerminals[agentId] = instance;
        if (usingFallback) _fallbackTerminals.Add(agentId);

        var capturedUsingFallback = usingFallback;
        Callable.From(() =>
        {
            instance.Visible = true;
            WriteToTerminal(instance, capturedUsingFallback, $"● Agent {agentId} connected\n");
            DebugLog($"Terminal created for {agentId} (fallback={capturedUsingFallback})");

            if (_pendingOutput.TryGetValue(agentId, out var pending))
            {
                foreach (var line in pending)
                    WriteToTerminal(instance, capturedUsingFallback, line + "\n");
                _pendingOutput.Remove(agentId);
                DebugLog($"Flushed {pending.Count} buffered lines for {agentId}");
            }

            try
            {
                var timestamp = System.DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var castPath = System.IO.Path.Combine(_recordingsDir, $"{agentId}-{timestamp}.cast");
                var recorder = new GiantIsopod.Plugin.Process.AsciicastRecorder(castPath, 120, 24);
                _agentRecorders[agentId] = recorder;
                recorder.WriteOutput($"\u001b[32m● Agent {agentId} connected\u001b[0m\r\n");
            }
            catch (System.Exception ex)
            {
                DebugLog($"Failed to start recording for {agentId}: {ex.Message}");
            }
        }).CallDeferred();
    }

    public void AppendOutput(string agentId, string line, bool showingTerminal, string? selectedAgentId)
    {
        if (!_agentTerminals.TryGetValue(agentId, out var term))
        {
            if (!_pendingOutput.TryGetValue(agentId, out var pending))
            {
                pending = new List<string>();
                _pendingOutput[agentId] = pending;
            }
            pending.Add(line);

            if (!_agentMarkdownBuffers.TryGetValue(agentId, out var buf))
            {
                buf = new System.Text.StringBuilder();
                _agentMarkdownBuffers[agentId] = buf;
            }
            buf.AppendLine(line);
            return;
        }

        var isFallback = _fallbackTerminals.Contains(agentId);
        WriteToTerminal((Control)term, isFallback, line + "\n");

        var text = ColorizeOutput(line) + "\r\n";
        if (_agentRecorders.TryGetValue(agentId, out var recorder))
            recorder.WriteOutput(text);

        if (!_agentMarkdownBuffers.TryGetValue(agentId, out var buffer))
        {
            buffer = new System.Text.StringBuilder();
            _agentMarkdownBuffers[agentId] = buffer;
        }
        buffer.AppendLine(line);

        if (!showingTerminal && selectedAgentId == agentId)
            RefreshMarkdown(agentId);
    }

    public void ShowAgent(string agentId)
    {
        if (!_agentTerminals.ContainsKey(agentId))
            CreateTerminalForAgent(agentId);
        if (!_agentMarkdownLabels.ContainsKey(agentId))
            CreateMarkdownLabelForAgent(agentId);

        foreach (var (id, term) in _agentTerminals)
        {
            if (term is Control c)
                c.Visible = id == agentId;
        }
        foreach (var (id, label) in _agentMarkdownLabels)
            label.Visible = id == agentId;
    }

    public void RefreshMarkdown(string agentId)
    {
        if (!_agentMarkdownBuffers.TryGetValue(agentId, out var buffer)) return;
        if (!_agentMarkdownLabels.TryGetValue(agentId, out var label))
            label = CreateMarkdownLabelForAgent(agentId);

        var bbcode = MarkdownBBCode.Convert(buffer.ToString());
        label.Clear();
        label.AppendText("");
        label.ParseBbcode(bbcode);
    }

    public void CleanupAgent(string agentId)
    {
        if (_agentTerminals.TryGetValue(agentId, out var term) && term is Control termControl)
        {
            termControl.QueueFree();
            _agentTerminals.Remove(agentId);
            _fallbackTerminals.Remove(agentId);
        }
        if (_agentMarkdownLabels.TryGetValue(agentId, out var label))
        {
            label.QueueFree();
            _agentMarkdownLabels.Remove(agentId);
        }
        _agentMarkdownBuffers.Remove(agentId);
        _pendingOutput.Remove(agentId);

        if (_agentRecorders.TryGetValue(agentId, out var recorder))
        {
            _ = recorder.DisposeAsync();
            _agentRecorders.Remove(agentId);
        }
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

    private static Control CreateFallbackTerminal()
    {
        var container = new PanelContainer();
        var bg = new StyleBoxFlat();
        bg.BgColor = new Color(0.06f, 0.07f, 0.1f);
        bg.ContentMarginLeft = 6;
        bg.ContentMarginRight = 6;
        bg.ContentMarginTop = 4;
        bg.ContentMarginBottom = 4;
        container.AddThemeStyleboxOverride("panel", bg);

        var rtl = new RichTextLabel();
        rtl.Name = "FallbackTerminal";
        rtl.BbcodeEnabled = true;
        rtl.FitContent = false;
        rtl.ScrollFollowing = true;
        rtl.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        rtl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        rtl.AddThemeColorOverride("default_color", new Color(0.82f, 0.84f, 0.9f));
        rtl.AddThemeFontSizeOverride("normal_font_size", 13);
        container.AddChild(rtl);

        return container;
    }

    private static void WriteToTerminal(Control terminal, bool isFallback, string text)
    {
        if (isFallback)
        {
            var rtl = terminal.GetNodeOrNull<RichTextLabel>("FallbackTerminal");
            rtl?.AppendText(text);
        }
        else
        {
            var colorized = ColorizeOutput(text.TrimEnd('\n', '\r')) + "\r\n";
            terminal.Call("write_text", colorized);
        }
    }

    public static string ColorizeOutput(string text)
    {
        if (text.Contains("🔧") || text.Contains("tool_use"))
            return $"\u001b[33m{text}\u001b[0m";
        if (text.Contains("💭"))
            return $"\u001b[36m{text}\u001b[0m";
        if (text.TrimStart().StartsWith("##"))
            return $"\u001b[1;32m{text}\u001b[0m";
        if (text.TrimStart().StartsWith("```"))
            return $"\u001b[90m{text}\u001b[0m";
        if (text.TrimStart().StartsWith("- ") || text.TrimStart().StartsWith("* "))
            return $"\u001b[37m{text}\u001b[0m";
        if (text.Contains("error") || text.Contains("Error"))
            return $"\u001b[31m{text}\u001b[0m";
        return text;
    }

    private void DebugLog(string message)
    {
        try
        {
            System.IO.File.AppendAllText(_consoleLogPath,
                $"[{System.DateTime.Now:HH:mm:ss.fff}] {message}\n");
        }
        catch { /* ignore file errors */ }
    }
}
