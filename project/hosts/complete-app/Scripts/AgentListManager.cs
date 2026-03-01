using Godot;
using GiantIsopod.Contracts.Core;

namespace GiantIsopod.Hosts.CompleteApp;

/// <summary>
/// Manages the agent list panel: adding/removing agent entries,
/// selection state, and status dot color updates.
/// </summary>
public sealed class AgentListManager
{
    public event System.Action<string>? OnAgentRemoveClicked;

    private VBoxContainer? _agentList;
    private Label? _agentCountLabel;
    private readonly Dictionary<string, AgentHudEntry> _entries = new();
    private readonly HashSet<string> _activeRuntimes = new();

    private Label? _runtimeCountLabel;

    public int AgentCount => _entries.Count;

    public void Initialize(VBoxContainer agentList, Label agentCountLabel, Label runtimeCountLabel)
    {
        _agentList = agentList;
        _agentCountLabel = agentCountLabel;
        _runtimeCountLabel = runtimeCountLabel;
    }

    public void AddAgent(string agentId, AgentVisualInfo info)
    {
        if (_entries.ContainsKey(agentId) || _agentList == null) return;

        var entry = new AgentHudEntry(agentId, info.DisplayName);
        entry.OnRemoveClicked += () => OnAgentRemoveClicked?.Invoke(agentId);
        _agentList.AddChild(entry.Root);
        _entries[agentId] = entry;
        UpdateCount();
    }

    public void RemoveAgent(string agentId)
    {
        if (!_entries.TryGetValue(agentId, out var entry)) return;
        entry.Root.QueueFree();
        _entries.Remove(agentId);
        UpdateCount();
    }

    public void UpdateAgentState(string agentId, AgentActivityState state)
    {
        if (_entries.TryGetValue(agentId, out var entry))
            entry.SetState(state);
    }

    public void SetRuntimeConnected(string agentId, bool connected)
    {
        if (connected)
            _activeRuntimes.Add(agentId);
        else
            _activeRuntimes.Remove(agentId);

        UpdateRuntimeCount();

        if (_entries.TryGetValue(agentId, out var entry))
            entry.SetConnected(connected);
    }

    private void UpdateCount()
    {
        if (_agentCountLabel != null)
            _agentCountLabel.Text = $"Agents: {_entries.Count}";
    }

    private void UpdateRuntimeCount()
    {
        if (_runtimeCountLabel != null)
            _runtimeCountLabel.Text = $"Runtimes: {_activeRuntimes.Count}";
    }
}
