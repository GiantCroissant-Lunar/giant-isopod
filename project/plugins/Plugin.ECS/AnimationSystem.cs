using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using GiantIsopod.Contracts.ECS;

namespace GiantIsopod.Plugin.ECS;

/// <summary>
/// Updates sprite animation frames based on activity state and movement.
/// Delta time is stored as system state, set externally before each update.
/// </summary>
public class AnimationSystem : QuerySystem<AgentVisual, ActivityState, Movement>
{
    private const float FrameRate = 6f; // frames per second

    public float DeltaTime { get; set; }

    protected override void OnUpdate()
    {
        var dt = DeltaTime;

        Query.ForEachEntity((ref AgentVisual visual, ref ActivityState state, ref Movement mov, Entity _) =>
        {
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
        });
    }
}
