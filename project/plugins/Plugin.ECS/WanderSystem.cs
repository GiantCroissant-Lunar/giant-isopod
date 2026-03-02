using Friflo.Engine.ECS;
using GiantIsopod.Contracts.ECS;

namespace GiantIsopod.Plugin.ECS;

/// <summary>
/// Assigns random wander targets to idle agents periodically.
/// </summary>
public class WanderSystem : ISystem
{
    private readonly Random _rng = new();
    private float _timer;

    private const int MinTileX = 4;
    private const int MaxTileX = 56;
    private const int MinTileY = 4;
    private const int MaxTileY = 30;

    public float DeltaTime { get; set; }

    public void Update(EntityStore store)
    {
        _timer -= DeltaTime;
        if (_timer > 0) return;
        _timer = 1.5f + (float)_rng.NextDouble() * 2f;

        var query = store.Query<WorldPosition, Movement, ActivityState>();

        foreach (var (positions, movements, states, _) in query.Chunks)
        {
            for (int i = 0; i < positions.Length; i++)
            {
                ref var pos = ref positions[i];
                ref var mov = ref movements[i];
                ref var act = ref states[i];

                if (act.Current != Activity.Idle || mov.HasTarget) continue;

                // Random chance to start wandering
                if (_rng.NextDouble() > 0.4) continue;

                mov.TargetTileX = (int)(pos.X / 16f) + _rng.Next(-4, 5);
                mov.TargetTileY = (int)(pos.Y / 16f) + _rng.Next(-3, 4);
                mov.TargetTileX = Math.Clamp(mov.TargetTileX, MinTileX, MaxTileX);
                mov.TargetTileY = Math.Clamp(mov.TargetTileY, MinTileY, MaxTileY);
                mov.HasTarget = true;
            }
        }
    }
}
