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

    public void Update(EntityStore store)
    {
        if (_stateQueue.IsEmpty) return;

        var query = store.Query<ActivityState, AgentLink>();

        while (_stateQueue.TryDequeue(out var change))
        {
            if (!_agentIndexMap.TryGetValue(change.AgentId, out var index))
                continue;

            // Find and update the matching entity's ActivityState
            bool found = false;
            foreach (var (states, links, _) in query.Chunks)
            {
                if (found) break;
                for (int i = 0; i < states.Length; i++)
                {
                    ref var state = ref states[i];
                    ref var link = ref links[i];

                    if (link.AgentIndex == index)
                    {
                        state.Current = MapState(change.State);
                        state.StateTime = 0f;
                        found = true;
                        break;
                    }
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
