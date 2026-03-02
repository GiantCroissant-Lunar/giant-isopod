using Friflo.Engine.ECS;

namespace GiantIsopod.Plugin.ECS;

/// <summary>
/// Simple system interface for Friflo ECS systems.
/// </summary>
public interface ISystem
{
    void Update(EntityStore store);
}
