using Akka.Actor;
using GiantIsopod.Contracts.Core;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// /user/agents/{name}/rpc — manages the CliWrap pipe to pi --mode rpc.
/// Reads stdout events and forwards them to the parent AgentActor.
/// </summary>
public sealed class AgentRpcActor : UntypedActor
{
    private readonly string _agentId;
    private readonly string _piExecutable;
    private IAgentProcess? _process;
    private CancellationTokenSource? _cts;

    public AgentRpcActor(string agentId, string piExecutable)
    {
        _agentId = agentId;
        _piExecutable = piExecutable;
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
                // Forward to parent (AgentActor)
                Context.Parent.Tell(evt);
                break;
        }
    }

    private void StartPiProcess()
    {
        _cts = new CancellationTokenSource();
        // Process creation delegated to Plugin.Process.PiRpcClient
        // The actual CliWrap integration happens there
        // For now, signal that we're ready
        Context.Parent.Tell(new ProcessStarted(_agentId, 0));
        // TODO: Inject IAgentProcess via DI or factory, start reading events
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
