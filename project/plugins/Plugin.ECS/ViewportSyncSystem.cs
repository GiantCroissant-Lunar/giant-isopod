using System.Collections.Concurrent;
using Friflo.Engine.ECS;
using GiantIsopod.Contracts.Core;
using GiantIsopod.Contracts.ECS;

namespace GiantIsopod.Plugin.ECS;

/// <summary>
/// Drains actor system messages from a thread-safe queue and updates ECS components.
/// This is the bridge between Akka.NET (async) and ECS (frame-driven).
/// </summary>
public class ViewportSyncSystem : ISystem
{
    private readonly ConcurrentQueue<AgentStateChanged> _stateQueue = new();
    private readonly Dictionary<string, int> _agentIndexMap = new();
    private readonly Dictionary<int, AgentActivityState> _pending = new();

    /// <summary>
    /// Called by the ViewportActor to enqueue state changes from the actor system.
    /// Thread-safe.
    /// </summary>
    public void EnqueueStateChange(AgentStateChanged change)
    {
        _stateQueue.Enqueue(change);
    }

    public void RegisterAgent(string agentId, int entityIndex)
    {
        _agentIndexMap[agentId] = entityIndex;
    }

    /// <summary>
    /// Unregisters an agent when it is removed. Idempotent - no exception if agent not found.
    /// </summary>
    public void UnregisterAgent(string agentId)
    {
        _agentIndexMap.Remove(agentId);
    }

    public void Update(EntityStore store)
    {
        if (_stateQueue.IsEmpty) return;

        // Coalesce the latest change per agent index to avoid O(changes × entities)
        _pending.Clear();
        while (_stateQueue.TryDequeue(out var change))
        {
            if (_agentIndexMap.TryGetValue(change.AgentId, out var index))
            {
                _pending[index] = change.State; // overwrite to keep latest
            }
        }

        if (_pending.Count == 0) return;

        var query = store.Query<ActivityState, AgentLink>();

        foreach (var (states, links, _) in query.Chunks)
        {
            for (int i = 0; i < states.Length; i++)
            {
                ref var link = ref links[i];
                if (_pending.TryGetValue(link.AgentIndex, out var stateChange))
                {
                    ref var state = ref states[i];
                    state.Current = MapState(stateChange);
                    state.StateTime = 0f;
                }
            }
        }
    }

    private static Activity MapState(AgentActivityState state) => state switch
    {
        AgentActivityState.Idle => Activity.Idle,
        AgentActivityState.Walking => Activity.Walking,
        AgentActivityState.Typing => Activity.Typing,
        AgentActivityState.Reading => Activity.Reading,
        AgentActivityState.Waiting => Activity.Waiting,
        AgentActivityState.Thinking => Activity.Thinking,
        _ => Activity.Idle
    };
}
