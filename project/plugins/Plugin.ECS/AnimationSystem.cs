using Friflo.Engine.ECS;
using GiantIsopod.Contracts.ECS;

namespace GiantIsopod.Plugin.ECS;

/// <summary>
/// Updates sprite animation frames based on activity state and movement.
/// Delta time is stored as system state, set externally before each update.
/// </summary>
public class AnimationSystem : ISystem
{
    private const float FrameRate = 6f; // frames per second

    public float DeltaTime { get; set; }

    public void Update(EntityStore store)
    {
        var dt = DeltaTime;
        var query = store.Query<AgentVisual, ActivityState, Movement>();

        foreach (var (visuals, states, movements, _) in query.Chunks)
        {
            for (int i = 0; i < visuals.Length; i++)
            {
                ref var visual = ref visuals[i];
                ref var state = ref states[i];
                ref var mov = ref movements[i];

                state.StateTime += dt;
                var frameIndex = (int)(state.StateTime * FrameRate);

                // Update facing direction based on movement
                if (mov.VelocityX > 0.1f) visual.Facing = Direction.Right;
                else if (mov.VelocityX < -0.1f) visual.Facing = Direction.Left;
                else if (mov.VelocityY > 0.1f) visual.Facing = Direction.Down;
                else if (mov.VelocityY < -0.1f) visual.Facing = Direction.Up;

                // Animation frame depends on state
                visual.AnimationFrame = state.Current switch
                {
                    Activity.Idle => frameIndex % 2,
                    Activity.Walking => frameIndex % 4,
                    Activity.Typing => frameIndex % 3,
                    Activity.Reading => frameIndex % 2,
                    Activity.Waiting => frameIndex % 2,
                    Activity.Thinking => frameIndex % 2,
                    _ => 0
                };
            }
        }
    }
}
