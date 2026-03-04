using Akka.Actor;
using GiantIsopod.Contracts.Core;
using GiantIsopod.Contracts.Protocol.AgUi;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GiantIsopod.Plugin.Actors.Tests;

public class ViewportActorTests
{
    [Fact]
    public void TaskGraphEvents_AreMappedToAgUiLifecycleEvents()
    {
        using var system = ActorSystem.Create("viewport-tests");
        var bridge = new RecordingViewportBridge();
        var actor = system.ActorOf(Props.Create(() => new ViewportActor(NullLogger<ViewportActor>.Instance)));

        actor.Tell(new SetViewportBridge(bridge));
        actor.Tell(new NotifyTaskGraphSubmitted(
            "graph-1",
            new[] { new TaskNode("task-1", "desc", new HashSet<string> { "code_edit" }) },
            Array.Empty<TaskEdge>()));
        actor.Tell(new NotifyTaskNodeStatusChanged("graph-1", "task-1", TaskNodeStatus.Planning));
        actor.Tell(new NotifyTaskNodeStatusChanged("graph-1", "task-1", TaskNodeStatus.Validating, "pi-1"));
        actor.Tell(new TaskGraphCompleted("graph-1", new Dictionary<string, bool> { ["task-1"] = true }));

        SpinWait.SpinUntil(() => bridge.AgUiEvents.Count >= 4, TimeSpan.FromSeconds(2));

        Assert.Contains(bridge.AgUiEvents, e => e.AgentId == "graph:graph-1" && e.Event is RunStartedEvent started && started.RunId == "graph-1");
        Assert.Contains(bridge.AgUiEvents, e => e.AgentId == "graph:graph-1" && e.Event is StepStartedEvent step && step.RunId == "task-1" && step.StepName == "planning");
        Assert.Contains(bridge.AgUiEvents, e => e.AgentId == "pi-1" && e.Event is StepStartedEvent step && step.RunId == "task-1" && step.StepName == "validation");
        Assert.Contains(bridge.AgUiEvents, e => e.AgentId == "graph:graph-1" && e.Event is RunFinishedEvent finished && finished.RunId == "graph-1");
    }

    private sealed class RecordingViewportBridge : IViewportBridge
    {
        public List<(string AgentId, object Event)> AgUiEvents { get; } = new();

        public void PublishAgentStateChanged(string agentId, AgentActivityState state) { }
        public void PublishAgentSpawned(string agentId, AgentVisualInfo visualInfo) { }
        public void PublishAgentDespawned(string agentId) { }
        public void PublishGenUIRequest(string agentId, string a2uiJson) { }
        public void PublishRuntimeStarted(string agentId, int processId) { }
        public void PublishRuntimeExited(string agentId, int exitCode) { }
        public void PublishRuntimeOutput(string agentId, string line) { }
        public void PublishAgUiEvent(string agentId, object agUiEvent) => AgUiEvents.Add((agentId, agUiEvent));
    }
}
