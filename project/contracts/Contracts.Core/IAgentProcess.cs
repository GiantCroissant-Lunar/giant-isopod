namespace GiantIsopod.Contracts.Core;

/// <summary>
/// Manages a single agent CLI process (e.g., pi --mode rpc).
/// </summary>
public interface IAgentProcess : IAsyncDisposable
{
    string AgentId { get; }
    bool IsRunning { get; }

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task SendAsync(string message, CancellationToken ct = default);
    IAsyncEnumerable<string> ReadEventsAsync(CancellationToken ct = default);
}
