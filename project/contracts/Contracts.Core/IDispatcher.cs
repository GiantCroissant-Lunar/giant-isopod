namespace GiantIsopod.Contracts.Core;

/// <summary>
/// Routes tasks to agents based on capability requirements.
/// </summary>
public interface IDispatcher
{
    Task<string?> DispatchAsync(DispatchRequest request, CancellationToken ct = default);
}

public record DispatchRequest(
    string TaskId,
    IReadOnlySet<string> RequiredCapabilities,
    string? PreferredAgentId = null
);
