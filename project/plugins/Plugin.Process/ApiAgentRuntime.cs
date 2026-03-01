using GiantIsopod.Contracts.Core;

namespace GiantIsopod.Plugin.Process;

/// <summary>
/// HTTP/API-based agent runtime — stub, not yet implemented.
/// </summary>
public sealed class ApiAgentRuntime : IAgentRuntime
{
    public string AgentId { get; }
    public bool IsRunning => false;

    public ApiAgentRuntime(string agentId)
    {
        AgentId = agentId;
    }

    public Task StartAsync(CancellationToken ct = default)
        => throw new NotImplementedException("API runtime is not yet implemented.");

    public Task StopAsync(CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SendAsync(string message, CancellationToken ct = default)
        => throw new NotImplementedException("API runtime is not yet implemented.");

    public IAsyncEnumerable<string> ReadEventsAsync(CancellationToken ct = default)
        => throw new NotImplementedException("API runtime is not yet implemented.");

    public ValueTask DisposeAsync() => default;
}
