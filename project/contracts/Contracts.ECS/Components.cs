using Friflo.Engine.ECS;

namespace GiantIsopod.Contracts.ECS;

/// <summary>
/// World position on the tile grid.
/// </summary>
public struct WorldPosition : IComponent
{
    public float X;
    public float Y;
    public int TileX;
    public int TileY;
}

/// <summary>
/// Movement target and velocity for pathfinding.
/// </summary>
public struct Movement : IComponent
{
    public float VelocityX;
    public float VelocityY;
    public int TargetTileX;
    public int TargetTileY;
    public bool HasTarget;
}

/// <summary>
/// Current visual activity state, driven by actor system events.
/// </summary>
public struct ActivityState : IComponent
{
    public Activity Current;
    public float StateTime;
}

public enum Activity : byte
{
    Idle,
    Walking,
    Typing,
    Reading,
    Waiting,
    Thinking
}

/// <summary>
/// Visual appearance derived from AIEOS physicality.
/// </summary>
public struct AgentVisual : IComponent
{
    public int PaletteIndex;
    public int HueShift;
    public int AnimationFrame;
    public Direction Facing;
}

public enum Direction : byte
{
    Down,
    Left,
    Right,
    Up
}

/// <summary>
/// Links an ECS entity to its agent actor identity.
/// </summary>
public struct AgentLink : IComponent
{
    public int AgentIndex;
}

/// <summary>
/// String-based agent identity for lookup by agent ID.
/// </summary>
public struct AgentIdentity : IComponent
{
    public string AgentId;
    public string DisplayName;
}

/// <summary>
/// Seat/desk assignment for the agent character.
/// </summary>
public struct SeatAssignment : IComponent
{
    public int SeatTileX;
    public int SeatTileY;
    public bool IsSeated;
}

/// <summary>
/// Currently active skill being used (for HUD display).
/// </summary>
public struct ActiveSkill : IComponent
{
    public int SkillNameIndex;
}

/// <summary>
/// Visual indicator for memory store/recall activity.
/// </summary>
public struct MemoryIndicator : IComponent
{
    public MemoryActivity Activity;
    public float FadeTimer;
}

public enum MemoryActivity : byte
{
    None,
    Storing,
    Recalling
}
