using Friflo.Engine.ECS;
using GiantIsopod.Contracts.Core;
using GiantIsopod.Contracts.ECS;

namespace GiantIsopod.Plugin.ECS;

/// <summary>
/// Manages the ECS world for agent visualization using pure Friflo ECS.
/// Creates/destroys entities from viewport events, ticks movement and animation systems.
/// </summary>
public sealed class AgentEcsWorld
{
    private readonly EntityStore _store;
    private readonly MovementSystem _movement;
    private readonly AnimationSystem _animation;
    private readonly WanderSystem _wander;
    private readonly ViewportSyncSystem _viewportSync;

    // Agent ID → Entity lookup
    private readonly Dictionary<string, Entity> _agentEntities = new();

    // Stable unique index counter for AgentLink.AgentIndex
    private int _nextAgentIndex = 0;

    public AgentEcsWorld()
    {
        _store = new EntityStore();

        _movement = new MovementSystem();
        _animation = new AnimationSystem();
        _wander = new WanderSystem();
        _viewportSync = new ViewportSyncSystem();
    }

    public void Tick(float delta)
    {
        _movement.DeltaTime = delta;
        _animation.DeltaTime = delta;
        _wander.DeltaTime = delta;

        // Process viewport sync first so simulation systems see up-to-date ActivityState
        _viewportSync.Update(_store);
        _movement.Update(_store);
        _wander.Update(_store);
        _animation.Update(_store);
    }

    public Entity? SpawnAgent(string agentId, AgentVisualInfo info)
    {
        if (_agentEntities.ContainsKey(agentId)) return null;

        var paletteIndex = (int)((uint)agentId.GetHashCode() % 6);
        var rng = new Random(agentId.GetHashCode());

        var agentIndex = _nextAgentIndex++;

        var entity = _store.CreateEntity();
        entity.AddComponent(new AgentIdentity { AgentId = agentId, DisplayName = info.DisplayName });
        entity.AddComponent(new WorldPosition { X = 100 + rng.Next(600), Y = 80 + rng.Next(300) });
        entity.AddComponent(new Movement());
        entity.AddComponent(new ActivityState { Current = Activity.Idle });
        entity.AddComponent(new AgentVisual { PaletteIndex = paletteIndex, Facing = Direction.Down });
        entity.AddComponent(new AgentLink { AgentIndex = agentIndex });

        _agentEntities[agentId] = entity;
        _viewportSync.RegisterAgent(agentId, agentIndex);
        return entity;
    }

    public bool RemoveAgent(string agentId)
    {
        if (!_agentEntities.TryGetValue(agentId, out var entity)) return false;
        entity.DeleteEntity();
        _agentEntities.Remove(agentId);
        _viewportSync.UnregisterAgent(agentId);
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
        foreach (var (positions, visuals, states, identities, _) in query.Chunks)
        {
            for (int i = 0; i < positions.Length; i++)
            {
                ref var pos = ref positions[i];
                ref var vis = ref visuals[i];
                ref var act = ref states[i];
                ref var id = ref identities[i];
                callback(id.AgentId, pos, vis, act, id);
            }
        }
    }

    public int AgentCount => _agentEntities.Count;
    public IReadOnlyCollection<string> AgentIds => _agentEntities.Keys;

    /// <summary>
    /// Enqueues a state change from the actor system to be processed by ViewportSyncSystem.
    /// </summary>
    public void EnqueueStateChange(AgentStateChanged change)
    {
        _viewportSync.EnqueueStateChange(change);
    }
}
