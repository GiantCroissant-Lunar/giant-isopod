using Akka.Actor;
using GiantIsopod.Contracts.Core;
using GiantIsopod.Plugin.Process;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// /user/agents/{name}/rpc — manages the runtime pipe for an agent.
/// Creates the correct IAgentRuntime via RuntimeFactory and streams output
/// as RuntimeEvent messages to the parent AgentActor.
/// </summary>
public sealed class AgentRuntimeActor : UntypedActor
{
    private readonly string _agentId;
    private readonly string? _runtimeId;
    private readonly ModelSpec? _model;
    private readonly AgentWorldConfig _config;
    private IAgentRuntime? _runtime;
    private CancellationTokenSource? _cts;

    // Per-task token budget tracking (supports concurrent tasks)
    private readonly Dictionary<string, TokenBudgetState> _tokenBudgets = new();
    private string? _activeTaskId;

    public AgentRuntimeActor(string agentId, AgentWorldConfig config, string? runtimeId = null, ModelSpec? model = null)
    {
        _agentId = agentId;
        _config = config;
        _runtimeId = runtimeId;
        _model = model;
    }

    protected override void OnReceive(object message)
    {
        switch (message)
        {
            case StartRuntime:
                StartAgentRuntime();
                break;

            case SendPrompt prompt:
                _ = SendToRuntimeAsync(prompt.Message);
                break;

            case RuntimeEvent evt:
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

        if (estimatedTokens > budgetState.MaxTokens * 1.2)
        {
            Context.ActorSelection("../tasks")
                .Tell(new TokenBudgetExceeded(_activeTaskId, estimatedTokens, budgetState.MaxTokens));
            _cts?.Cancel();
        }
        else if (!budgetState.Warned && estimatedTokens > budgetState.MaxTokens)
        {
            budgetState.Warned = true;
            Context.ActorSelection("../tasks")
                .Tell(new TokenBudgetExceeded(_activeTaskId, estimatedTokens, budgetState.MaxTokens));
        }
    }

    private void StartAgentRuntime()
    {
        _cts = new CancellationTokenSource();

        var workDir = !string.IsNullOrEmpty(_config.RuntimeWorkingDirectory)
            ? _config.RuntimeWorkingDirectory
            : System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);

        var runtimeConfig = _config.Runtimes.ResolveOrDefault(_runtimeId ?? _config.DefaultRuntimeId);
        _runtime = RuntimeFactory.Create(_agentId, runtimeConfig, _model, workDir, _config.RuntimeEnvironment);

        var self = Self;
        var parent = Context.Parent;
        var ct = _cts.Token;

        _ = Task.Run(async () =>
        {
            parent.Tell(new RuntimeStarted(_agentId, System.Environment.ProcessId));

            bool started = false;
            try
            {
                await _runtime.StartAsync(ct);

                await foreach (var line in _runtime.ReadEventsAsync(ct))
                {
                    if (!started)
                        started = true;

                    if (!string.IsNullOrWhiteSpace(line))
                        self.Tell(new RuntimeEvent(_agentId, line));
                }

                parent.Tell(new RuntimeExited(_agentId, 0));
            }
            catch (OperationCanceledException)
            {
                if (started)
                    parent.Tell(new RuntimeExited(_agentId, -1));
            }
            catch (Exception ex)
            {
                self.Tell(new RuntimeEvent(_agentId, $"[ERROR] Runtime failed: {ex.Message}"));
                parent.Tell(new RuntimeExited(_agentId, -1));
            }
        }, ct);
    }

    private async Task SendToRuntimeAsync(string message)
    {
        if (_runtime is { IsRunning: true })
        {
            await _runtime.SendAsync(message, _cts?.Token ?? CancellationToken.None);
        }
    }

    protected override void PostStop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _ = _runtime?.DisposeAsync();
    }
}

/// <summary>Sets the token budget for the active task on this runtime actor.</summary>
public record SetTokenBudget(string TaskId, int MaxTokens);

internal sealed class TokenBudgetState(int maxTokens)
{
    public int MaxTokens { get; } = maxTokens;
    public long CumulativeChars { get; set; }
    public bool Warned { get; set; }
}
