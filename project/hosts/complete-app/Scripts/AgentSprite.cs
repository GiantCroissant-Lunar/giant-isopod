using Godot;
using GiantIsopod.Contracts.Core;
using GiantIsopod.Contracts.ECS;

namespace GiantIsopod.Hosts.CompleteApp;

/// <summary>
/// Visual representation of an agent in the 2D world.
/// Position and state are driven by ECS — this node only renders.
/// </summary>
public partial class AgentSprite : Node2D
{
    [Signal]
    public delegate void AgentClickedEventHandler(string agentId);

    private readonly string _agentId;
    private readonly AgentVisualInfo _info;
    private AgentActivityState _state = AgentActivityState.Idle;
    private int _animFrame;

    private float _bobPhase;
    private float _pulsePhase;

    private static readonly Color[] AgentColors =
    [
        new(0.23f, 0.51f, 0.96f),
        new(0.16f, 0.73f, 0.44f),
        new(0.91f, 0.30f, 0.24f),
        new(0.61f, 0.35f, 0.85f),
        new(0.95f, 0.61f, 0.07f),
        new(0.07f, 0.78f, 0.82f),
    ];

    private Color _color;
    private Label? _nameLabel;
    private Label? _stateLabel;

    public AgentSprite(string agentId, AgentVisualInfo info)
    {
        _agentId = agentId;
        _info = info;
        _color = AgentColors[(uint)agentId.GetHashCode() % (uint)AgentColors.Length];
    }

    public override void _Ready()
    {
        _nameLabel = new Label
        {
            Text = _info.DisplayName,
            HorizontalAlignment = HorizontalAlignment.Center,
            Position = new Vector2(-40, -55),
        };
        _nameLabel.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.95f));
        _nameLabel.AddThemeFontSizeOverride("font_size", 13);
        AddChild(_nameLabel);

        _stateLabel = new Label
        {
            Text = "idle",
            HorizontalAlignment = HorizontalAlignment.Center,
            Position = new Vector2(-40, 28),
        };
        _stateLabel.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.55f));
        _stateLabel.AddThemeFontSizeOverride("font_size", 11);
        AddChild(_stateLabel);
    }

    /// <summary>
    /// Called by Main.SyncSprites each frame with ECS data.
    /// </summary>
    public void SyncFromEcs(AgentActivityState state, int animFrame, Direction facing)
    {
        if (_state != state)
        {
            _state = state;
            UpdateStateLabel();
        }
        _animFrame = animFrame;
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        _bobPhase += (float)delta * 2.0f;
        _pulsePhase += (float)delta * 3.0f;
        QueueRedraw();
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb)
        {
            // Use GetGlobalMousePosition for correct world-space coords with viewport scaling
            var worldMouse = GetGlobalMousePosition();
            var local = worldMouse - GlobalPosition;
            if (local.LengthSquared() < 28f * 28f)
            {
                EmitSignal(SignalName.AgentClicked, _agentId);
                GetViewport().SetInputAsHandled();
            }
        }
    }

    private void UpdateStateLabel()
    {
        if (_stateLabel == null) return;

        _stateLabel.Text = _state switch
        {
            AgentActivityState.Idle => "idle",
            AgentActivityState.Walking => "walking",
            AgentActivityState.Typing => "typing ⌨",
            AgentActivityState.Reading => "reading 📖",
            AgentActivityState.Waiting => "waiting ⏳",
            AgentActivityState.Thinking => "thinking 💭",
            _ => "..."
        };
        _stateLabel.RemoveThemeColorOverride("font_color");
        _stateLabel.AddThemeColorOverride("font_color", _state switch
        {
            AgentActivityState.Typing => new Color(0.3f, 0.85f, 0.35f),
            AgentActivityState.Thinking => new Color(0.9f, 0.75f, 0.2f),
            AgentActivityState.Reading => new Color(0.35f, 0.65f, 0.95f),
            AgentActivityState.Waiting => new Color(0.6f, 0.6f, 0.65f),
            _ => new Color(0.5f, 0.5f, 0.55f)
        });
    }

    public override void _Draw()
    {
        float bob = Mathf.Sin(_bobPhase) * 3.0f;
        float radius = 18f;

        DrawCircle(new Vector2(2, 4 + bob), radius, new Color(0, 0, 0, 0.3f));

        var bodyColor = _color;
        if (_state == AgentActivityState.Thinking)
        {
            float pulse = (Mathf.Sin(_pulsePhase) + 1f) * 0.15f;
            bodyColor = bodyColor.Lightened(pulse);
        }
        DrawCircle(new Vector2(0, bob), radius, bodyColor);
        DrawCircle(new Vector2(-4, -4 + bob), radius * 0.35f, bodyColor.Lightened(0.3f));

        if (_state != AgentActivityState.Idle)
        {
            var ringColor = _state switch
            {
                AgentActivityState.Typing => new Color(0.3f, 0.85f, 0.35f, 0.6f),
                AgentActivityState.Thinking => new Color(0.9f, 0.75f, 0.2f, 0.6f),
                AgentActivityState.Reading => new Color(0.35f, 0.65f, 0.95f, 0.6f),
                AgentActivityState.Waiting => new Color(0.6f, 0.6f, 0.65f, 0.4f),
                _ => new Color(1, 1, 1, 0.3f)
            };
            float ringRadius = radius + 4f + Mathf.Sin(_pulsePhase) * 2f;
            DrawArc(new Vector2(0, bob), ringRadius, 0, Mathf.Tau, 32, ringColor, 2f);
        }
    }
}
