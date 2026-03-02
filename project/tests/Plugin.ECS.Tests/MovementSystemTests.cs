using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using GiantIsopod.Contracts.ECS;

namespace GiantIsopod.Plugin.ECS.Tests;

public class MovementSystemTests
{
    private readonly EntityStore _store;
    private readonly SystemRoot _root;
    private readonly MovementSystem _movement;

    public MovementSystemTests()
    {
        _store = new EntityStore();
        _movement = new MovementSystem();
        _root = new SystemRoot(_store) { _movement };
    }

    [Fact]
    public void NoTarget_NoMovement()
    {
        var entity = _store.CreateEntity(
            new WorldPosition { X = 100, Y = 100, TileX = 6, TileY = 6 },
            new Movement { HasTarget = false });

        _movement.DeltaTime = 0.016f;
        _root.Update();

        ref var pos = ref entity.GetComponent<WorldPosition>();
        Assert.Equal(100, pos.X);
        Assert.Equal(100, pos.Y);
    }

    [Fact]
    public void WithTarget_MovesTowardTarget()
    {
        var entity = _store.CreateEntity(
            new WorldPosition { X = 100, Y = 100, TileX = 6, TileY = 6 },
            new Movement { HasTarget = true, TargetTileX = 10, TargetTileY = 6 });

        _movement.DeltaTime = 0.1f;
        _root.Update();

        ref var pos = ref entity.GetComponent<WorldPosition>();
        // Target pixel is 10*16+8 = 168. Agent should have moved right (X increased)
        Assert.True(pos.X > 100, "Agent should move right toward target");
    }

    [Fact]
    public void ArrivalSnapsToTarget_ClearsHasTarget()
    {
        // Place entity very close to target pixel (target = 10*16+8=168, 6*16+8=104)
        var entity = _store.CreateEntity(
            new WorldPosition { X = 167, Y = 103.5f, TileX = 10, TileY = 6 },
            new Movement { HasTarget = true, TargetTileX = 10, TargetTileY = 6 });

        _movement.DeltaTime = 0.016f;
        _root.Update();

        ref var pos = ref entity.GetComponent<WorldPosition>();
        ref var mov = ref entity.GetComponent<Movement>();

        Assert.Equal(168f, pos.X);
        Assert.Equal(104f, pos.Y);
        Assert.Equal(10, pos.TileX);
        Assert.Equal(6, pos.TileY);
        Assert.False(mov.HasTarget);
        Assert.Equal(0, mov.VelocityX);
        Assert.Equal(0, mov.VelocityY);
    }

    [Fact]
    public void AlreadyAtTarget_SnapsImmediately()
    {
        var entity = _store.CreateEntity(
            new WorldPosition { X = 168, Y = 104, TileX = 10, TileY = 6 },
            new Movement { HasTarget = true, TargetTileX = 10, TargetTileY = 6 });

        _movement.DeltaTime = 0.016f;
        _root.Update();

        ref var mov = ref entity.GetComponent<Movement>();
        Assert.False(mov.HasTarget);
    }

    [Fact]
    public void VelocityDirection_IsNormalized()
    {
        // Target is directly to the right: target pixel X=168, Y=104
        var entity = _store.CreateEntity(
            new WorldPosition { X = 100, Y = 104, TileX = 6, TileY = 6 },
            new Movement { HasTarget = true, TargetTileX = 10, TargetTileY = 6 });

        _movement.DeltaTime = 0.016f;
        _root.Update();

        ref var mov = ref entity.GetComponent<Movement>();
        // Moving straight right: VelocityX should be ~64 (MoveSpeed), VelocityY ~0
        Assert.True(mov.VelocityX > 60, "Horizontal velocity should be near MoveSpeed");
        Assert.True(MathF.Abs(mov.VelocityY) < 1, "Vertical velocity should be near zero");
    }

    [Fact]
    public void DiagonalMovement_SpeedDoesNotExceedMoveSpeed()
    {
        // Diagonal target
        var entity = _store.CreateEntity(
            new WorldPosition { X = 100, Y = 100, TileX = 6, TileY = 6 },
            new Movement { HasTarget = true, TargetTileX = 20, TargetTileY = 20 });

        _movement.DeltaTime = 0.016f;
        _root.Update();

        ref var mov = ref entity.GetComponent<Movement>();
        var speed = MathF.Sqrt(mov.VelocityX * mov.VelocityX + mov.VelocityY * mov.VelocityY);
        Assert.InRange(speed, 63f, 65f); // should be ~64 (MoveSpeed)
    }

    [Fact]
    public void MultipleEntities_ProcessedIndependently()
    {
        var e1 = _store.CreateEntity(
            new WorldPosition { X = 100, Y = 100 },
            new Movement { HasTarget = true, TargetTileX = 20, TargetTileY = 6 });
        var e2 = _store.CreateEntity(
            new WorldPosition { X = 200, Y = 200 },
            new Movement { HasTarget = false });

        _movement.DeltaTime = 0.1f;
        _root.Update();

        ref var pos1 = ref e1.GetComponent<WorldPosition>();
        ref var pos2 = ref e2.GetComponent<WorldPosition>();

        Assert.NotEqual(100, pos1.X); // e1 moved
        Assert.Equal(200, pos2.X);     // e2 stayed
    }
}
