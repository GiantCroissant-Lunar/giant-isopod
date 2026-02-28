namespace GiantIsopod.Contracts.Skills;

/// <summary>
/// Set of capability identifiers required to execute a task or action.
/// Used by the dispatcher to find agents whose skill-derived capabilities satisfy the requirement.
/// </summary>
public record CapabilityRequirement
{
    public required IReadOnlySet<string> RequiredCapabilities { get; init; }
    public string? Description { get; init; }

    public bool IsSatisfiedBy(IReadOnlySet<string> agentCapabilities)
        => RequiredCapabilities.IsSubsetOf(agentCapabilities);

    public IReadOnlySet<string> UnmetBy(IReadOnlySet<string> agentCapabilities)
        => RequiredCapabilities.Except(agentCapabilities).ToHashSet();
}
