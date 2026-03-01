using Godot;
using GiantIsopod.Contracts.Core;

namespace GiantIsopod.Hosts.CompleteApp;

/// <summary>
/// GraphEdit-based read-only DAG visualization for task graphs.
/// Shows task nodes with status colors and dependency edges.
/// </summary>
public partial class TaskGraphView : Control
{
    private GraphEdit? _graphEdit;
    private Label? _titleLabel;

    // Track nodes per graph for status updates
    private readonly Dictionary<string, Dictionary<string, GraphNode>> _graphNodes = new();
    private readonly Dictionary<string, IReadOnlyList<TaskEdge>> _graphEdges = new();

    private static readonly Dictionary<TaskNodeStatus, Color> StatusColors = new()
    {
        [TaskNodeStatus.Pending] = new Color(0.5f, 0.5f, 0.5f),     // gray
        [TaskNodeStatus.Ready] = new Color(0.3f, 0.5f, 0.9f),       // blue
        [TaskNodeStatus.Dispatched] = new Color(0.9f, 0.8f, 0.2f),  // yellow
        [TaskNodeStatus.Completed] = new Color(0.3f, 0.8f, 0.3f),   // green
        [TaskNodeStatus.Failed] = new Color(0.9f, 0.25f, 0.25f),    // red
        [TaskNodeStatus.Cancelled] = new Color(0.35f, 0.35f, 0.35f) // dark gray
    };

    public override void _Ready()
    {
        _titleLabel = new Label
        {
            Text = "Task Graph",
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _titleLabel.AddThemeColorOverride("font_color", new Color(0.8f, 0.85f, 0.9f));
        _titleLabel.AddThemeFontSizeOverride("font_size", 16);
        AddChild(_titleLabel);

        _graphEdit = new GraphEdit
        {
            Position = new Vector2(0, 24),
            Size = new Vector2(Size.X, Size.Y - 24),
            RightDisconnects = false,
            MinimapEnabled = false,
        };
        // Read-only: prevent user from moving/connecting nodes
        _graphEdit.GuiInput += OnGraphEditInput;
        AddChild(_graphEdit);

        Visible = false;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized && _graphEdit != null)
        {
            _graphEdit.Size = new Vector2(Size.X, Size.Y - 24);
        }
    }

    private static void OnGraphEditInput(InputEvent evt)
    {
        // Block user editing — this is a read-only display
        if (evt is InputEventMouseButton)
            evt.Dispose();
    }

    public void HandleGraphSubmitted(string graphId, IReadOnlyList<TaskNode> nodes, IReadOnlyList<TaskEdge> edges)
    {
        if (_graphEdit == null) return;

        // Clear previous graph if any for this graphId
        ClearGraph(graphId);

        var nodeDict = new Dictionary<string, GraphNode>();
        _graphEdges[graphId] = edges;

        // Compute topological layers for layout
        var layers = ComputeLayers(nodes, edges);

        foreach (var node in nodes)
        {
            var gNode = CreateTaskNode(node);
            var layer = layers.GetValueOrDefault(node.TaskId, 0);
            var layerIndex = nodes.Where(n => layers.GetValueOrDefault(n.TaskId, 0) == layer)
                .ToList().IndexOf(node);

            gNode.PositionOffset = new Vector2(layer * 280, layerIndex * 120);
            _graphEdit.AddChild(gNode);
            nodeDict[node.TaskId] = gNode;
        }

        // Connect edges
        foreach (var edge in edges)
        {
            if (nodeDict.ContainsKey(edge.FromTaskId) && nodeDict.ContainsKey(edge.ToTaskId))
            {
                _graphEdit.ConnectNode(
                    nodeDict[edge.FromTaskId].Name,
                    0,
                    nodeDict[edge.ToTaskId].Name,
                    0);
            }
        }

        _graphNodes[graphId] = nodeDict;

        if (_titleLabel != null)
            _titleLabel.Text = $"Task Graph: {graphId}";

        Visible = true;
    }

    public void HandleNodeStatusChanged(string graphId, string taskId, TaskNodeStatus status, string? agentId)
    {
        if (!_graphNodes.TryGetValue(graphId, out var nodeDict)) return;
        if (!nodeDict.TryGetValue(taskId, out var gNode)) return;

        // Update status label
        var statusLabel = gNode.GetNodeOrNull<Label>("StatusLabel");
        if (statusLabel != null)
        {
            var statusText = status.ToString();
            if (agentId != null)
                statusText += $" ({agentId})";
            statusLabel.Text = statusText;
        }

        // Update node color via self-modulate
        if (StatusColors.TryGetValue(status, out var color))
        {
            gNode.SelfModulate = color;
        }
    }

    public void HandleGraphCompleted(string graphId, IReadOnlyDictionary<string, bool> results)
    {
        var succeeded = results.Values.Count(v => v);
        if (_titleLabel != null)
            _titleLabel.Text = $"Task Graph: {graphId} — {succeeded}/{results.Count} succeeded";
    }

    private GraphNode CreateTaskNode(TaskNode task)
    {
        var gNode = new GraphNode
        {
            Title = task.TaskId,
            Name = $"TaskNode_{task.TaskId.Replace(" ", "_")}",
            Resizable = false,
            SelfModulate = StatusColors[TaskNodeStatus.Pending],
        };

        // Set valid connection types for edges
        gNode.SetSlot(0, true, 0, Colors.White, true, 0, Colors.White);

        var descLabel = new Label
        {
            Text = task.Description.Length > 40
                ? task.Description[..40] + "..."
                : task.Description,
            CustomMinimumSize = new Vector2(200, 0),
        };
        descLabel.AddThemeFontSizeOverride("font_size", 12);
        gNode.AddChild(descLabel);

        var statusLabel = new Label
        {
            Name = "StatusLabel",
            Text = "Pending",
        };
        statusLabel.AddThemeFontSizeOverride("font_size", 11);
        statusLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
        gNode.AddChild(statusLabel);

        return gNode;
    }

    private void ClearGraph(string graphId)
    {
        if (!_graphNodes.TryGetValue(graphId, out var nodeDict)) return;

        if (_graphEdit != null)
        {
            // Disconnect all edges first
            if (_graphEdges.TryGetValue(graphId, out var edges))
            {
                foreach (var edge in edges)
                {
                    if (nodeDict.ContainsKey(edge.FromTaskId) && nodeDict.ContainsKey(edge.ToTaskId))
                    {
                        _graphEdit.DisconnectNode(
                            nodeDict[edge.FromTaskId].Name,
                            0,
                            nodeDict[edge.ToTaskId].Name,
                            0);
                    }
                }
            }

            foreach (var gNode in nodeDict.Values)
                gNode.QueueFree();
        }

        _graphNodes.Remove(graphId);
        _graphEdges.Remove(graphId);
    }

    /// <summary>
    /// Assigns each node to a topological layer (roots at layer 0).
    /// </summary>
    private static Dictionary<string, int> ComputeLayers(
        IReadOnlyList<TaskNode> nodes,
        IReadOnlyList<TaskEdge> edges)
    {
        var incoming = new Dictionary<string, HashSet<string>>();
        var outgoing = new Dictionary<string, List<string>>();

        foreach (var node in nodes)
        {
            incoming[node.TaskId] = new HashSet<string>();
            outgoing[node.TaskId] = new List<string>();
        }

        foreach (var edge in edges)
        {
            if (incoming.ContainsKey(edge.ToTaskId) && outgoing.ContainsKey(edge.FromTaskId))
            {
                incoming[edge.ToTaskId].Add(edge.FromTaskId);
                outgoing[edge.FromTaskId].Add(edge.ToTaskId);
            }
        }

        var layers = new Dictionary<string, int>();
        var queue = new Queue<string>();

        // Start with roots (no incoming edges)
        foreach (var (taskId, deps) in incoming)
        {
            if (deps.Count == 0)
            {
                layers[taskId] = 0;
                queue.Enqueue(taskId);
            }
        }

        // BFS layer assignment
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var currentLayer = layers[current];

            foreach (var dep in outgoing.GetValueOrDefault(current, []))
            {
                var newLayer = currentLayer + 1;
                if (!layers.TryGetValue(dep, out var existing) || existing < newLayer)
                {
                    layers[dep] = newLayer;
                }

                // Only enqueue when all incoming are assigned
                if (incoming[dep].All(d => layers.ContainsKey(d)))
                {
                    if (!queue.Contains(dep))
                        queue.Enqueue(dep);
                }
            }
        }

        return layers;
    }
}
