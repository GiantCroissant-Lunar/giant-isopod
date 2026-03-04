using Akka.Actor;
using Akka.TestKit.Xunit2;
using GiantIsopod.Contracts.Core;
using GiantIsopod.Plugin.Actors;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json.Nodes;
using System.Text.Json;
using Xunit;

namespace GiantIsopod.Plugin.Actors.Tests;

public sealed class A2AActorTests : TestKit
{
    private readonly Akka.TestKit.TestProbe _dispatchProbe;
    private readonly Akka.TestKit.TestProbe _registryProbe;
    private readonly IActorRef _a2a;

    public A2AActorTests()
    {
        _dispatchProbe = CreateTestProbe();
        _registryProbe = CreateTestProbe();
        _a2a = Sys.ActorOf(Props.Create(() =>
            new A2AActor(_dispatchProbe.Ref, _registryProbe.Ref, NullLogger<A2AActor>.Instance)));
    }

    [Fact]
    public void SendTask_ReturnsSchemaBackedSubmittedTaskJson()
    {
        _a2a.Tell(new A2ASendTask("task-1", "planner-1", "Decompose this feature.", "{\"graphId\":\"g1\"}"), TestActor);

        _dispatchProbe.ExpectMsg<TaskRequest>(msg => msg.TaskId == "task-1");
        var result = ExpectMsg<A2ATaskResult>();
        var task = JsonNode.Parse(result.StatusJson)!.AsObject();

        Assert.Equal("task-1", task["taskId"]!.GetValue<string>());
        Assert.Equal("Submitted", task["status"]!.GetValue<string>());
        var history = task["history"]!.AsArray();
        Assert.Single(history);
        Assert.Equal("user", history[0]!["role"]!.GetValue<string>());
    }

    [Fact]
    public void AssignedAndCompletedTask_EmitsWorkingThenCompletedSchema()
    {
        _a2a.Tell(new A2ASendTask("task-2", "planner-1", "Implement change."), TestActor);
        _dispatchProbe.ExpectMsg<TaskRequest>(msg => msg.TaskId == "task-2");
        ExpectMsg<A2ATaskResult>();

        _a2a.Tell(new TaskAssigned("task-2", "agent-claude"));
        _a2a.Tell(new A2AGetTask("task-2"), TestActor);
        var working = JsonNode.Parse(ExpectMsg<A2ATaskResult>().StatusJson)!.AsObject();
        Assert.Equal("Working", working["status"]!.GetValue<string>());

        _a2a.Tell(new TaskCompleted(
            "task-2",
            "agent-claude",
            true,
            "Done.",
            Artifacts:
            [
                new ArtifactRef(
                    "artifact-1",
                    ArtifactType.Code,
                    "text/plain",
                    "file:///project/src/Feature.cs",
                    "hash",
                    new ArtifactProvenance("task-2", "agent-claude", DateTimeOffset.UtcNow),
                    new Dictionary<string, string> { ["relativePath"] = "project/src/Feature.cs" })
            ]));

        _a2a.Tell(new A2AGetTask("task-2"), TestActor);
        var completed = JsonNode.Parse(ExpectMsg<A2ATaskResult>().StatusJson)!.AsObject();
        Assert.Equal("Completed", completed["status"]!.GetValue<string>());
        var artifacts = completed["artifacts"]!.AsArray();
        Assert.Single(artifacts);
        Assert.Equal("project/src/Feature.cs", artifacts[0]!["name"]!.GetValue<string>());
    }
}
