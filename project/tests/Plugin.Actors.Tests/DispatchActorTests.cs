using Akka.Actor;
using Akka.TestKit.Xunit2;
using GiantIsopod.Contracts.Core;
using GiantIsopod.Plugin.Actors;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GiantIsopod.Plugin.Actors.Tests;

public class DispatchActorTests : TestKit
{
    private readonly Akka.TestKit.TestProbe _registryProbe;
    private readonly Akka.TestKit.TestProbe _agentSupervisorProbe;
    private readonly Akka.TestKit.TestProbe _workspaceProbe;
    private readonly IActorRef _dispatch;

    public DispatchActorTests()
    {
        _registryProbe = CreateTestProbe();
        _agentSupervisorProbe = CreateTestProbe();
        _workspaceProbe = CreateTestProbe();

        _dispatch = Sys.ActorOf(Props.Create(() =>
            new DispatchActor(
                _registryProbe.Ref,
                _agentSupervisorProbe.Ref,
                _workspaceProbe.Ref,
                NullLogger<DispatchActor>.Instance)));
    }

    [Fact]
    public void ResolveBidSession_SelectsHighestFitnessThenLowestLoadThenShortestDuration()
    {
        _dispatch.Tell(new TaskRequest("task-1", "implement feature", Caps("code_edit")), TestActor);

        var query = _registryProbe.ExpectMsg<QueryCapableAgents>();
        Assert.Equal(Caps("code_edit"), query.RequiredCapabilities);

        _registryProbe.Reply(new CapableAgentsResult(new[] { "alpha", "beta", "gamma" }));

        ExpectTaskAvailable("alpha", "task-1");
        ExpectTaskAvailable("beta", "task-1");
        ExpectTaskAvailable("gamma", "task-1");

        _dispatch.Tell(new TaskBid("task-1", "gamma", 0.70, 0, TimeSpan.FromMinutes(1)));
        _dispatch.Tell(new TaskBid("task-1", "alpha", 0.95, 0, TimeSpan.FromMinutes(5)));
        _dispatch.Tell(new TaskBid("task-1", "beta", 0.95, 0, TimeSpan.FromMinutes(2)));

        var allocation = _workspaceProbe.ExpectMsg<AllocateWorkspace>(TimeSpan.FromSeconds(2));
        Assert.Equal("task-1", allocation.TaskId);
        _workspaceProbe.Reply(new WorkspaceAllocated("task-1", @"C:\temp\wt-task-1", "swarm/task-1"));

        var loser1 = _agentSupervisorProbe.ExpectMsg<ForwardToAgent>(TimeSpan.FromSeconds(2));
        var loser2 = _agentSupervisorProbe.ExpectMsg<ForwardToAgent>(TimeSpan.FromSeconds(2));
        var assignment = _agentSupervisorProbe.ExpectMsg<TaskAssigned>(TimeSpan.FromSeconds(2));
        var requesterAssignment = ExpectMsg<TaskAssigned>(TimeSpan.FromSeconds(2));

        Assert.Equal("beta", assignment.AgentId);
        Assert.Equal("beta", requesterAssignment.AgentId);
        Assert.Equal(@"C:\temp\wt-task-1", assignment.WorkspacePath);

        var rejectedAgents = new[]
        {
            ExtractRejectedBidAgent(loser1, "task-1"),
            ExtractRejectedBidAgent(loser2, "task-1")
        };

        Assert.Contains("alpha", rejectedAgents);
        Assert.Contains("gamma", rejectedAgents);
    }

    [Fact]
    public void ResolveBidSession_FallsBackToFirstCapableAgentWhenNoBidsArrive()
    {
        _dispatch.Tell(new TaskRequest("task-2", "implement feature", Caps("code_edit")), TestActor);

        _registryProbe.ExpectMsg<QueryCapableAgents>();
        _registryProbe.Reply(new CapableAgentsResult(new[] { "alpha", "beta" }));

        ExpectTaskAvailable("alpha", "task-2");
        ExpectTaskAvailable("beta", "task-2");

        var allocation = _workspaceProbe.ExpectMsg<AllocateWorkspace>(TimeSpan.FromSeconds(2));
        Assert.Equal("task-2", allocation.TaskId);
        _workspaceProbe.Reply(new WorkspaceAllocated("task-2", @"C:\temp\wt-task-2", "swarm/task-2"));

        var assignment = _agentSupervisorProbe.ExpectMsg<TaskAssigned>(TimeSpan.FromSeconds(2));
        var requesterAssignment = ExpectMsg<TaskAssigned>(TimeSpan.FromSeconds(2));

        Assert.Equal("alpha", assignment.AgentId);
        Assert.Equal("alpha", requesterAssignment.AgentId);
        _agentSupervisorProbe.ExpectNoMsg(TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public void HandleBid_IgnoresNonCapableAndDuplicateBids()
    {
        _dispatch.Tell(new TaskRequest("task-3", "implement feature", Caps("code_edit")), TestActor);

        _registryProbe.ExpectMsg<QueryCapableAgents>();
        _registryProbe.Reply(new CapableAgentsResult(new[] { "alpha", "beta" }));

        ExpectTaskAvailable("alpha", "task-3");
        ExpectTaskAvailable("beta", "task-3");

        _dispatch.Tell(new TaskBid("task-3", "outsider", 1.00, 0, TimeSpan.FromMinutes(1)));
        _dispatch.Tell(new TaskBid("task-3", "alpha", 0.80, 0, TimeSpan.FromMinutes(2)));
        _dispatch.Tell(new TaskBid("task-3", "alpha", 0.99, 0, TimeSpan.FromSeconds(10)));
        _dispatch.Tell(new TaskBid("task-3", "beta", 0.90, 0, TimeSpan.FromMinutes(1)));

        var allocation = _workspaceProbe.ExpectMsg<AllocateWorkspace>(TimeSpan.FromSeconds(2));
        Assert.Equal("task-3", allocation.TaskId);
        _workspaceProbe.Reply(new WorkspaceAllocated("task-3", @"C:\temp\wt-task-3", "swarm/task-3"));

        var loser = _agentSupervisorProbe.ExpectMsg<ForwardToAgent>(TimeSpan.FromSeconds(2));
        var assignment = _agentSupervisorProbe.ExpectMsg<TaskAssigned>(TimeSpan.FromSeconds(2));
        var requesterAssignment = ExpectMsg<TaskAssigned>(TimeSpan.FromSeconds(2));

        Assert.Equal("beta", assignment.AgentId);
        Assert.Equal("beta", requesterAssignment.AgentId);
        Assert.Equal("alpha", ExtractRejectedBidAgent(loser, "task-3"));
        _agentSupervisorProbe.ExpectNoMsg(TimeSpan.FromMilliseconds(200));
    }

    private static HashSet<string> Caps(params string[] caps) => new(caps);

    private void ExpectTaskAvailable(string expectedAgentId, string expectedTaskId)
    {
        var forward = _agentSupervisorProbe.ExpectMsg<ForwardToAgent>(TimeSpan.FromSeconds(2));
        Assert.Equal(expectedAgentId, forward.AgentId);
        var available = Assert.IsType<TaskAvailable>(forward.Payload);
        Assert.Equal(expectedTaskId, available.TaskId);
    }

    private static string ExtractRejectedBidAgent(ForwardToAgent forward, string expectedTaskId)
    {
        var rejected = Assert.IsType<TaskBidRejected>(forward.Payload);
        Assert.Equal(expectedTaskId, rejected.TaskId);
        return rejected.AgentId;
    }
}
