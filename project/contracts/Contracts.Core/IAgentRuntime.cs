namespace GiantIsopod.Contracts.Core;

/// <summary>
/// Manages a single agent runtime (CLI subprocess, API client, SDK session, etc.).
/// </summary>
public interface IAgentRuntime : IAsyncDisposable
{
    string AgentId { get; }
    bool IsRunning { get; }

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task SendAsync(string message, CancellationToken ct = default);
    IAsyncEnumerable<string> ReadEventsAsync(CancellationToken ct = default);
}
