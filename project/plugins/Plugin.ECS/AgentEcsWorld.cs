using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using GiantIsopod.Contracts.Core;
using GiantIsopod.Contracts.ECS;

namespace GiantIsopod.Plugin.ECS;

/// <summary>
/// Manages the Friflo ECS world for agent visualization.
/// Creates/destroys entities from viewport events, ticks movement and animation systems.
/// </summary>
public sealed class AgentEcsWorld
{
    private readonly EntityStore _store;
    private readonly SystemRoot _systems;
    private readonly MovementSystem _movement;
    private readonly AnimationSystem _animation;
    private readonly WanderSystem _wander;

    // Agent ID → Entity lookup
    private readonly Dictionary<string, Entity> _agentEntities = new();

    public AgentEcsWorld()
    {
        _store = new EntityStore();

        _movement = new MovementSystem();
        _animation = new AnimationSystem();
        _wander = new WanderSystem();

        _systems = new SystemRoot(_store, "AgentWorld")
        {
            _movement,
            _animation,
            _wander,
        };
    }

    public void Tick(float delta)
    {
        _movement.DeltaTime = delta;
        _animation.DeltaTime = delta;
        _wander.DeltaTime = delta;
        _systems.Update(default);
    }

    public Entity? SpawnAgent(string agentId, AgentVisualInfo info)
    {
        if (_agentEntities.ContainsKey(agentId)) return null;

        var paletteIndex = (int)((uint)agentId.GetHashCode() % 6);
        var rng = new Random(agentId.GetHashCode());

        var entity = _store.CreateEntity(
            new AgentIdentity { AgentId = agentId, DisplayName = info.DisplayName },
            new WorldPosition { X = 100 + rng.Next(600), Y = 80 + rng.Next(300) },
            new Movement(),
            new ActivityState { Current = Activity.Idle },
            new AgentVisual { PaletteIndex = paletteIndex, Facing = Direction.Down },
            new AgentLink { AgentIndex = _agentEntities.Count });

        _agentEntities[agentId] = entity;
        return entity;
    }

    public bool RemoveAgent(string agentId)
    {
        if (!_agentEntities.TryGetValue(agentId, out var entity)) return false;
        entity.DeleteEntity();
        _agentEntities.Remove(agentId);
        return true;
    }

    public void SetAgentActivity(string agentId, Activity activity)
    {
        if (!_agentEntities.TryGetValue(agentId, out var entity)) return;
        entity.GetComponent<ActivityState>().Current = activity;
        entity.GetComponent<ActivityState>().StateTime = 0;
    }

    public bool HasAgent(string agentId) => _agentEntities.ContainsKey(agentId);

    /// <summary>
    /// Iterates all agent entities for rendering. Callback receives agent ID, position, visual, and activity.
    /// </summary>
    public void ForEachAgent(Action<string, WorldPosition, AgentVisual, ActivityState, AgentIdentity> callback)
    {
        var query = _store.Query<WorldPosition, AgentVisual, ActivityState, AgentIdentity>();
        query.ForEachEntity((ref WorldPosition pos, ref AgentVisual vis, ref ActivityState act, ref AgentIdentity id, Entity _) =>
        {
            callback(id.AgentId, pos, vis, act, id);
        });
    }

    public int AgentCount => _agentEntities.Count;
    public IReadOnlyCollection<string> AgentIds => _agentEntities.Keys;
}
