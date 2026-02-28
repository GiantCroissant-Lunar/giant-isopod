using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using GiantIsopod.Contracts.ECS;

namespace GiantIsopod.Plugin.ECS;

/// <summary>
/// Moves entities toward their target tile using simple pathfinding.
/// Delta time is stored as system state, set externally before each update.
/// </summary>
public class MovementSystem : QuerySystem<WorldPosition, Movement>
{
    private const float MoveSpeed = 64f; // pixels per second
    private const float ArrivalThreshold = 2f;

    public float DeltaTime { get; set; }

    protected override void OnUpdate()
    {
        var dt = DeltaTime;

        Query.ForEachEntity((ref WorldPosition pos, ref Movement mov, Entity _) =>
        {
            if (!mov.HasTarget) return;

            var targetX = mov.TargetTileX * 16f + 8f;
            var targetY = mov.TargetTileY * 16f + 8f;
            var dx = targetX - pos.X;
            var dy = targetY - pos.Y;
            var dist = MathF.Sqrt(dx * dx + dy * dy);

            if (dist < ArrivalThreshold)
            {
                pos.X = targetX;
                pos.Y = targetY;
                pos.TileX = mov.TargetTileX;
                pos.TileY = mov.TargetTileY;
                mov.HasTarget = false;
                mov.VelocityX = 0;
                mov.VelocityY = 0;
                return;
            }

            var nx = dx / dist;
            var ny = dy / dist;
            mov.VelocityX = nx * MoveSpeed;
            mov.VelocityY = ny * MoveSpeed;
            pos.X += mov.VelocityX * dt;
            pos.Y += mov.VelocityY * dt;
        });
    }
}
