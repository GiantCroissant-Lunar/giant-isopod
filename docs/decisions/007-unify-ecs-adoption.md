# ADR-007: UnifyECS Adoption Plan

## Status

Proposed (deferred — adopt when ECS surface area grows)

## Context

Giant-Isopod uses **Friflo.Engine.ECS 3.0.0-preview.13** directly for agent
visualization. The ECS footprint is small: 9 components, 4 systems, ~10-20
entities. All ECS code lives in two projects:

- `Contracts.ECS` — component structs implementing `Friflo.Engine.ECS.IComponent`
- `Plugin.ECS` — `AgentEcsWorld` + systems inheriting `QuerySystem<T1,T2,...>`

The **unify-ecs** plate-project (`C:\lunar-horse\plate-projects\unify-ecs`)
provides a source-generation-based abstraction layer over multiple ECS backends
(Arch, Flecs, Friflo) with zero runtime overhead. Adopting it would decouple
giant-isopod from any single ECS framework.

## Decision

Adopt UnifyECS **when the ECS surface area justifies it** — specifically when
any of these triggers occur:

1. Adding 3+ new systems beyond the current 4
2. Needing to benchmark alternative backends (Arch, Flecs)
3. Sharing ECS code across multiple projects with different backends
4. Friflo preview instability forces a backend switch

Until then, keep the direct Friflo dependency but **structure new ECS code** to
minimize migration friction (see Migration Guide below).

## Packages

UnifyECS ships as multiple NuGet packages from `plate-projects/unify-ecs`:

| Package | Role | Giant-Isopod needs? |
|---------|------|---------------------|
| `UnifyEcs.Core` | `IWorld`, `Entity`, interfaces | Yes |
| `UnifyEcs.Attributes` | `[EcsComponent]`, `[EcsSystem]`, `[Query]` | Yes |
| `UnifyEcs.Generators` | Roslyn source generator | Yes (dev dependency) |
| `UnifyEcs.Analyzers` | Compile-time diagnostics | Yes (dev dependency) |
| `UnifyEcs.Runtime.Friflo` | Friflo backend adapter | Yes (keeps current backend) |
| `UnifyEcs.Runtime.Arch` | Arch backend adapter | Optional (benchmarking) |
| `UnifyEcs.Runtime.Flecs` | Flecs backend adapter | Optional (benchmarking) |

Current packaged version: **0.1.0** (in `plate-projects/unify-ecs/build/_artifacts/`).

To pack and sync to local feed:
```bash
cd C:\lunar-horse\plate-projects\unify-ecs
task pack
cp build/_artifacts/*/nuget/*.nupkg C:\lunar-horse\packages\nuget\flat/
```

## Migration Guide

### Phase 1: Components (Contracts.ECS)

Replace `Friflo.Engine.ECS.IComponent` with `[EcsComponent]` attribute.

**Before (current):**
```csharp
using Friflo.Engine.ECS;

public struct WorldPosition : IComponent
{
    public float X;
    public float Y;
    public int TileX;
    public int TileY;
}
```

**After (UnifyECS):**
```csharp
using UnifyEcs.Attributes;

[EcsComponent]
public struct WorldPosition
{
    public float X;
    public float Y;
    public int TileX;
    public int TileY;
}
```

Apply to all 9 components. Enums (`Activity`, `Direction`, `MemoryActivity`)
need no changes — they are plain C# types, not ECS constructs.

**csproj change** for `Contracts.ECS.csproj`:
```xml
<!-- Remove -->
<PackageReference Include="Friflo.Engine.ECS" Version="3.0.0-preview.13" />

<!-- Add -->
<PackageReference Include="UnifyEcs.Attributes" Version="0.1.0" />
```

### Phase 2: Systems (Plugin.ECS)

Replace `QuerySystem<T1,T2>` inheritance with `[EcsSystem]` + `[Query]` attributes.

**Before (current):**
```csharp
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;

public class MovementSystem : QuerySystem<WorldPosition, Movement>
{
    public float DeltaTime { get; set; }

    protected override void OnUpdate()
    {
        var dt = DeltaTime;
        Query.ForEachEntity((ref WorldPosition pos, ref Movement mov, Entity _) =>
        {
            if (!mov.HasTarget) return;
            // ... movement logic
        });
    }
}
```

**After (UnifyECS):**
```csharp
using UnifyEcs.Attributes;

[EcsSystem(Phase = SystemPhase.Update)]
public partial class MovementSystem
{
    private const float MoveSpeed = 64f;
    private const float ArrivalThreshold = 2f;

    [Query(All = new[] { typeof(WorldPosition), typeof(Movement) })]
    public void Process(ref WorldPosition pos, ref Movement mov, float deltaTime)
    {
        if (!mov.HasTarget) return;
        // ... movement logic (identical)
    }
}
```

Note: `deltaTime` is injected by the framework — no more manual property setting.

Apply the same pattern to `AnimationSystem`, `WanderSystem`, `ViewportSyncSystem`.

**csproj change** for `Plugin.ECS.csproj`:
```xml
<!-- Remove -->
<PackageReference Include="Friflo.Engine.ECS" Version="3.0.0-preview.13" />

<!-- Add -->
<PackageReference Include="UnifyEcs.Core" Version="0.1.0" />
<PackageReference Include="UnifyEcs.Attributes" Version="0.1.0" />
<PackageReference Include="UnifyEcs.Generators" Version="0.1.0" />
<PackageReference Include="UnifyEcs.Analyzers" Version="0.1.0" />
<PackageReference Include="UnifyEcs.Runtime.Friflo" Version="0.1.0" />
```

### Phase 3: World Management (AgentEcsWorld)

Replace direct `EntityStore` usage with `IWorld`.

**Before (current):**
```csharp
private readonly EntityStore _store;

// Create entity
var entity = _store.CreateEntity(
    new AgentIdentity { ... },
    new WorldPosition { ... },
    ...);

// Query
var query = _store.Query<WorldPosition, AgentVisual, ActivityState, AgentIdentity>();
query.ForEachEntity((ref WorldPosition pos, ...) => { ... });

// Delete
entity.DeleteEntity();
```

**After (UnifyECS):**
```csharp
[Inject] private IWorld _world;

// Create entity
var entity = _world.CreateEntity();
_world.Add(entity, new AgentIdentity { ... });
_world.Add(entity, new WorldPosition { ... });
// ... add remaining components

// Query — handled by [Query] methods in systems

// Delete
_world.DestroyEntity(entity);
```

### Phase 4: SystemRoot → UnifyECS Pipeline

Replace manual `SystemRoot` with framework-managed system discovery.

The current manual wiring:
```csharp
_systems = new SystemRoot(_store, "AgentWorld")
{
    _movement,
    _animation,
    _wander,
};
_movement.DeltaTime = delta;
_systems.Update(default);
```

Becomes framework-managed via `[EcsSystem(Phase = ...)]` attributes — systems
are auto-discovered and ordered by `Phase` and `Order` properties.

## What Does NOT Change

These patterns are outside the ECS layer and remain unchanged:

- **Godot integration** — `Main.cs` sprite sync loop, lazy sprite creation
- **Actor↔ECS bridge** — `ConcurrentQueue<AgentStateChanged>` pattern
- **Agent ID mapping** — `Dictionary<string, Entity>` lookup stays in `AgentEcsWorld`
- **Rendering pipeline** — `ForEachAgent` callback remains a thin wrapper

## Risks

| Risk | Mitigation |
|------|------------|
| Source generator build complexity | UnifyECS generators are dev-only deps; won't affect runtime |
| Preview-on-preview (Friflo preview + UnifyECS 0.1.0) | Run spike on one system first; keep Friflo fallback branch |
| Delta time injection may differ from manual pattern | Verify in spike that `float deltaTime` param injection works |
| `AgentIdentity` has managed `string` fields | Confirm UnifyECS handles managed components via `[EcsComponent]` |

## Spike Plan

Before full migration, validate with a single-system spike:

1. Pack unify-ecs 0.1.0 to local NuGet feed
2. Create branch `spike/unify-ecs`
3. Port `MovementSystem` only (simplest — no managed types, pure math)
4. Verify build succeeds and movement behavior is identical
5. Measure build time delta (source generators add compilation overhead)

## Consequences

- **Contracts.ECS** drops its Friflo dependency entirely (only `UnifyEcs.Attributes`)
- **Plugin.ECS** swaps Friflo for UnifyECS packages (5 packages replace 1)
- Components become plain structs — portable across any backend
- Systems become partial classes — source generator fills in iteration code
- Backend can be swapped by changing one package reference (Runtime.Friflo → Runtime.Arch)
- Build gets ~2-5s slower due to source generation (acceptable for the flexibility gained)
