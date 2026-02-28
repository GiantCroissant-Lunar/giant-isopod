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
    private readonly AgentWorldConfig _config;
    private IAgentProcess? _process;
    private CancellationTokenSource? _cts;

    public AgentRpcActor(string agentId, AgentWorldConfig config)
    {
        _agentId = agentId;
        _config = config;
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
                Context.Parent.Tell(evt);
                break;
        }
    }

    private void StartPiProcess()
    {
        _cts = new CancellationTokenSource();

        // Use configured working directory, or fallback to user's home
        var workDir = !string.IsNullOrEmpty(_config.PiWorkingDirectory)
            ? _config.PiWorkingDirectory
            : System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        _process = new PiRpcClient(_agentId, _config.PiExecutable, workDir,
            _config.PiProvider, _config.PiModel, _config.PiEnvironment);

        var self = Self;
        var parent = Context.Parent;
        var ct = _cts.Token;

        _ = Task.Run(async () =>
        {
            bool started = false;
            try
            {
                await _process.StartAsync(ct);

                await foreach (var line in _process.ReadEventsAsync(ct))
                {
                    if (!started)
                    {
                        // First output confirms the process is actually running
                        started = true;
                        parent.Tell(new ProcessStarted(_agentId, System.Environment.ProcessId));
                    }

                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        self.Tell(new ProcessEvent(_agentId, line));
                    }
                }

                parent.Tell(new ProcessExited(_agentId, 0));
            }
            catch (OperationCanceledException)
            {
                if (started)
                    parent.Tell(new ProcessExited(_agentId, -1));
            }
            catch (Exception)
            {
                // pi executable not found or crashed — don't report as started
                if (started)
                    parent.Tell(new ProcessExited(_agentId, -1));
                // If never started, parent stays in demo mode (no ProcessStarted sent)
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
