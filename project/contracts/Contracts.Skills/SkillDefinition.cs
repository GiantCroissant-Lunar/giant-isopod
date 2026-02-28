namespace GiantIsopod.Contracts.Skills;

/// <summary>
/// Parsed SKILL.md following the Agent Skills specification (agentskills.io).
/// </summary>
public record SkillDefinition
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? License { get; init; }
    public SkillMetadata? Metadata { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> AllowedTools { get; init; } = [];
    public string? Scope { get; init; }
    public string Body { get; init; } = string.Empty;

    /// <summary>
    /// Derived capabilities from metadata.capabilities or legacy role mapping.
    /// </summary>
    public IReadOnlySet<string> DerivedCapabilities { get; init; } = new HashSet<string>();
}

public record SkillMetadata
{
    public string? Author { get; init; }
    public string? Version { get; init; }
    public IReadOnlyList<string> Capabilities { get; init; } = [];
}
