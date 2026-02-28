namespace GiantIsopod.Contracts.Skills;

/// <summary>
/// Full description of an agent: AIEOS identity + skill bundle + derived capabilities.
/// </summary>
public record AgentProfile
{
    public required string AgentId { get; init; }
    public required string DisplayName { get; init; }
    public required string SkillBundleName { get; init; }

    /// <summary>
    /// Union of capabilities from all skills in the assigned bundle.
    /// </summary>
    public IReadOnlySet<string> DerivedCapabilities { get; init; } = new HashSet<string>();

    /// <summary>
    /// Path to the AIEOS persona JSON file.
    /// </summary>
    public string? AieosProfilePath { get; init; }

    /// <summary>
    /// Path to the Memvid .mv2 memory file.
    /// </summary>
    public string? MemoryFilePath { get; init; }
}
