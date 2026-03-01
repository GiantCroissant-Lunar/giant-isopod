using Akka.Actor;
using GiantIsopod.Contracts.Core;
using GiantIsopod.Plugin.Process;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// /user/agents/{name}/rpc — manages the CliWrap pipe to pi --mode rpc.
/// Reads stdout events and forwards them to the parent AgentActor.
/// Reports ProcessStarted only after the child process is confirmed running.
/// </summary>
public sealed class AgentRpcActor : UntypedActor
{
    private readonly string _agentId;
    private readonly string? _cliProviderId;
    private readonly AgentWorldConfig _config;
    private IAgentProcess? _process;
    private CancellationTokenSource? _cts;

    // Per-task token budget tracking (supports concurrent tasks)
    private readonly Dictionary<string, TokenBudgetState> _tokenBudgets = new();
    private string? _activeTaskId; // most recently assigned task gets output attributed

    public AgentRpcActor(string agentId, AgentWorldConfig config, string? cliProviderId = null)
    {
        _agentId = agentId;
        _config = config;
        _cliProviderId = cliProviderId;
    }

    protected override void OnReceive(object message)
    {
        switch (message)
        {
            case StartProcess:
                StartPiProcess();
                break;

            case SendPrompt prompt:
                _ = SendToPiAsync(prompt.Message);
                break;

            case ProcessEvent evt:
                TrackTokenUsage(evt.RawJson);
                Context.Parent.Tell(evt);
                break;

            case SetTokenBudget budget:
                _activeTaskId = budget.TaskId;
                _tokenBudgets[budget.TaskId] = new TokenBudgetState(budget.MaxTokens);
                break;
        }
    }

    private void TrackTokenUsage(string output)
    {
        if (_activeTaskId is null || !_tokenBudgets.TryGetValue(_activeTaskId, out var budgetState))
            return;

        budgetState.CumulativeChars += output.Length;
        var estimatedTokens = (int)(budgetState.CumulativeChars / 4);

        // Kill process at 120% of budget
        if (estimatedTokens > budgetState.MaxTokens * 1.2)
        {
            Context.ActorSelection("../tasks")
                .Tell(new TokenBudgetExceeded(_activeTaskId, estimatedTokens, budgetState.MaxTokens));
            _cts?.Cancel();
        }
        // Warn at 100% of budget
        else if (!budgetState.Warned && estimatedTokens > budgetState.MaxTokens)
        {
            budgetState.Warned = true;
            Context.ActorSelection("../tasks")
                .Tell(new TokenBudgetExceeded(_activeTaskId, estimatedTokens, budgetState.MaxTokens));
        }
    }

    private void StartPiProcess()
    {
        _cts = new CancellationTokenSource();

        var workDir = !string.IsNullOrEmpty(_config.CliWorkingDirectory)
            ? _config.CliWorkingDirectory
            : System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);

        var provider = _config.CliProviders.ResolveOrDefault(_cliProviderId ?? _config.DefaultCliProviderId);
        _process = new CliAgentProcess(_agentId, provider, workDir, _config.CliEnvironment);

        var self = Self;
        var parent = Context.Parent;
        var ct = _cts.Token;

        _ = Task.Run(async () =>
        {
            parent.Tell(new ProcessStarted(_agentId, System.Environment.ProcessId));

            bool started = false;
            try
            {
                await _process.StartAsync(ct);

                await foreach (var line in _process.ReadEventsAsync(ct))
                {
                    if (!started)
                        started = true;

                    if (!string.IsNullOrWhiteSpace(line))
                        self.Tell(new ProcessEvent(_agentId, line));
                }

                parent.Tell(new ProcessExited(_agentId, 0));
            }
            catch (OperationCanceledException)
            {
                if (started)
                    parent.Tell(new ProcessExited(_agentId, -1));
            }
            catch (Exception ex)
            {
                self.Tell(new ProcessEvent(_agentId, $"[ERROR] CLI failed: {ex.Message}"));
                parent.Tell(new ProcessExited(_agentId, -1));
            }
        }, ct);
    }

    private async Task SendToPiAsync(string message)
    {
        if (_process is { IsRunning: true })
        {
            await _process.SendAsync(message, _cts?.Token ?? CancellationToken.None);
        }
    }

    protected override void PostStop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _ = _process?.DisposeAsync();
    }
}

/// <summary>Sets the token budget for the active task on this RPC actor.</summary>
public record SetTokenBudget(string TaskId, int MaxTokens);

internal sealed class TokenBudgetState(int maxTokens)
{
    public int MaxTokens { get; } = maxTokens;
    public long CumulativeChars { get; set; }
    public bool Warned { get; set; }
}
