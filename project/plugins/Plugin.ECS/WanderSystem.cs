using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using GiantIsopod.Contracts.ECS;

namespace GiantIsopod.Plugin.ECS;

/// <summary>
/// Assigns random wander targets to idle agents periodically.
/// </summary>
public class WanderSystem : QuerySystem<WorldPosition, Movement, ActivityState>
{
    private readonly Random _rng = new();
    private float _timer;

    public float DeltaTime { get; set; }

    protected override void OnUpdate()
    {
        _timer -= DeltaTime;
        if (_timer > 0) return;
        _timer = 1.5f + (float)_rng.NextDouble() * 2f;

        Query.ForEachEntity((ref WorldPosition pos, ref Movement mov, ref ActivityState act, Entity _) =>
        {
            if (act.Current != Activity.Idle || mov.HasTarget) return;

            // Random chance to start wandering
            if (_rng.NextDouble() > 0.4) return;

            mov.TargetTileX = (int)(pos.X / 16f) + _rng.Next(-4, 5);
            mov.TargetTileY = (int)(pos.Y / 16f) + _rng.Next(-3, 4);
            mov.TargetTileX = Math.Clamp(mov.TargetTileX, 12, 70);
            mov.TargetTileY = Math.Clamp(mov.TargetTileY, 8, 50);
            mov.HasTarget = true;
        });
    }
}
