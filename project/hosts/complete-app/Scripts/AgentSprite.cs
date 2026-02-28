using Godot;
using GiantIsopod.Contracts.Core;

namespace GiantIsopod.Hosts.CompleteApp;

/// <summary>
/// Visual representation of an agent in the 2D world.
/// Draws a colored circle with name label and activity indicator.
/// Wanders gently when idle, shows activity animations for other states.
/// </summary>
public partial class AgentSprite : Node2D
{
    private readonly string _agentId;
    private readonly AgentVisualInfo _info;
    private AgentActivityState _state = AgentActivityState.Idle;

    private Vector2 _wanderTarget;
    private float _wanderTimer;
    private float _bobPhase;
    private float _pulsePhase;

    private static readonly Color[] AgentColors =
    [
        new(0.23f, 0.51f, 0.96f),  // blue
        new(0.16f, 0.73f, 0.44f),  // green
        new(0.91f, 0.30f, 0.24f),  // red
        new(0.61f, 0.35f, 0.85f),  // purple
        new(0.95f, 0.61f, 0.07f),  // orange
        new(0.07f, 0.78f, 0.82f),  // teal
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
        _wanderTarget = Position;

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

    public void SetActivityState(AgentActivityState state)
    {
        _state = state;
        if (_stateLabel != null)
        {
            _stateLabel.Text = state switch
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
            _stateLabel.AddThemeColorOverride("font_color", state switch
            {
                AgentActivityState.Typing => new Color(0.3f, 0.85f, 0.35f),
                AgentActivityState.Thinking => new Color(0.9f, 0.75f, 0.2f),
                AgentActivityState.Reading => new Color(0.35f, 0.65f, 0.95f),
                AgentActivityState.Waiting => new Color(0.6f, 0.6f, 0.65f),
                _ => new Color(0.5f, 0.5f, 0.55f)
            });
        }
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        var dt = (float)delta;
        _bobPhase += dt * 2.0f;
        _pulsePhase += dt * 3.0f;

        // Gentle wandering when idle
        _wanderTimer -= dt;
        if (_wanderTimer <= 0 && _state == AgentActivityState.Idle)
        {
            var rng = new RandomNumberGenerator();
            _wanderTarget = Position + new Vector2(
                rng.RandfRange(-60, 60),
                rng.RandfRange(-40, 40));
            _wanderTimer = rng.RandfRange(2.0f, 5.0f);
        }

        if (_state == AgentActivityState.Idle || _state == AgentActivityState.Walking)
        {
            Position = Position.Lerp(_wanderTarget, dt * 0.5f);
        }

        QueueRedraw();
    }

    public override void _Draw()
    {
        float bob = Mathf.Sin(_bobPhase) * 3.0f;
        float radius = 18f;

        // Shadow
        DrawCircle(new Vector2(2, 4 + bob), radius, new Color(0, 0, 0, 0.3f));

        // Body circle
        var bodyColor = _color;
        if (_state == AgentActivityState.Thinking)
        {
            float pulse = (Mathf.Sin(_pulsePhase) + 1f) * 0.15f;
            bodyColor = bodyColor.Lightened(pulse);
        }
        DrawCircle(new Vector2(0, bob), radius, bodyColor);

        // Inner highlight
        DrawCircle(new Vector2(-4, -4 + bob), radius * 0.35f, bodyColor.Lightened(0.3f));

        // Activity ring
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
