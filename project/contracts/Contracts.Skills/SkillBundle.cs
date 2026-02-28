namespace GiantIsopod.Contracts.Skills;

/// <summary>
/// Named set of skill references whose combined capabilities satisfy a logical role.
/// Replaces the SwarmRole enum as the primary dispatch unit.
/// </summary>
public record SkillBundle
{
    public required string Name { get; init; }
    public IReadOnlyList<string> SkillNames { get; init; } = [];

    /// <summary>
    /// Union of all capabilities from the bundle's skills. Computed at registration time.
    /// </summary>
    public IReadOnlySet<string> DerivedCapabilities { get; init; } = new HashSet<string>();
}
