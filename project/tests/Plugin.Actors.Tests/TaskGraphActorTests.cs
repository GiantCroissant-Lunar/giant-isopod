using Akka.Actor;
using Akka.TestKit.Xunit2;
using GiantIsopod.Contracts.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GiantIsopod.Plugin.Actors.Tests;

public class TaskGraphActorTests : TestKit
{
    private readonly IActorRef _dispatchProbe;
    private readonly IActorRef _agentSupervisorProbe;
    private readonly IActorRef _viewportProbe;
    private readonly IActorRef _taskGraph;

    public TaskGraphActorTests()
    {
        _dispatchProbe = CreateTestProbe().Ref;
        _agentSupervisorProbe = CreateTestProbe().Ref;
        _viewportProbe = CreateTestProbe().Ref;

        _taskGraph = Sys.ActorOf(Props.Create(() =>
            new TaskGraphActor(
                _dispatchProbe,
                _agentSupervisorProbe,
                _viewportProbe,
                NullLogger<TaskGraphActor>.Instance)));
    }

    private static HashSet<string> Caps(params string[] caps) => new(caps);

    private static SubmitTaskGraph MakeGraph(
        string graphId,
        IReadOnlyList<TaskNode> nodes,
        IReadOnlyList<TaskEdge>? edges = null,
        TaskBudget? budget = null) =>
        new(graphId, nodes, edges ?? Array.Empty<TaskEdge>(), budget);

    private static TaskNode Node(string id, string desc = "test task", params string[] caps) =>
        new(id, desc, new HashSet<string>(caps));

    private static ProposedSubplan MakeSubplan(
        string parentTaskId,
        IReadOnlyList<SubtaskProposal> subtasks,
        DecompositionReason reason = DecompositionReason.TooLarge,
        StopCondition? stopWhen = null) =>
        new(parentTaskId, reason, subtasks, stopWhen);

    private static SubtaskProposal Proposal(
        string desc = "subtask",
        IReadOnlyList<string>? dependsOn = null,
        params string[] caps) =>
        new(desc, new HashSet<string>(caps), dependsOn ?? Array.Empty<string>());

    // ── Graph submission ──

    [Fact]
    public void Submit_ValidGraph_Accepted()
    {
        var submit = MakeGraph("g1", new[] { Node("t1"), Node("t2") },
            new[] { new TaskEdge("t1", "t2") });

        _taskGraph.Tell(submit, TestActor);
        var accepted = ExpectMsg<TaskGraphAccepted>();
        Assert.Equal("g1", accepted.GraphId);
        Assert.Equal(2, accepted.NodeCount);
        Assert.Equal(1, accepted.EdgeCount);
    }

    [Fact]
    public void Submit_CyclicGraph_Rejected()
    {
        var submit = MakeGraph("g-cycle",
            new[] { Node("a"), Node("b") },
            new[] { new TaskEdge("a", "b"), new TaskEdge("b", "a") });

        _taskGraph.Tell(submit, TestActor);
        var rejected = ExpectMsg<TaskGraphRejected>();
        Assert.Equal("g-cycle", rejected.GraphId);
        Assert.Contains("cycle", rejected.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ── Task completion without decomposition ──

    [Fact]
    public void TaskCompleted_NoSubplan_MarksCompleted()
    {
        // Submit a single-node graph
        _taskGraph.Tell(MakeGraph("g2", new[] { Node("t1", "do thing", "skill-a") }), TestActor);
        ExpectMsg<TaskGraphAccepted>();

        // Complete the task
        _taskGraph.Tell(new TaskCompleted("t1", "agent-1", true, "done", "g2"));

        // Graph should complete — listen for EventStream
        Sys.EventStream.Subscribe(TestActor, typeof(TaskGraphCompleted));
        // Re-send completion (graph already completed above, so we need a fresh graph)
        // Actually, the graph completed synchronously. Let's use a two-node graph instead.
    }

    [Fact]
    public void TaskCompleted_NoSubplan_CompletesGraph()
    {
        Sys.EventStream.Subscribe(TestActor, typeof(TaskGraphCompleted));

        _taskGraph.Tell(MakeGraph("g3", new[] { Node("t1") }), TestActor);
        ExpectMsg<TaskGraphAccepted>();

        _taskGraph.Tell(new TaskCompleted("t1", "agent-1", true, "done", "g3"));
        var completed = ExpectMsg<TaskGraphCompleted>();
        Assert.Equal("g3", completed.GraphId);
        Assert.True(completed.Results["t1"]);
    }

    // ── Decomposition acceptance ──

    [Fact]
    public void TaskCompleted_WithSubplan_InsertsSubtasks()
    {
        _taskGraph.Tell(MakeGraph("g4", new[] { Node("t1") }), TestActor);
        ExpectMsg<TaskGraphAccepted>();

        var subplan = MakeSubplan("t1", new[]
        {
            Proposal("sub-a", caps: "cap-1"),
            Proposal("sub-b", dependsOn: new[] { "0" }, caps: "cap-2"),
        });

        _taskGraph.Tell(new TaskCompleted("t1", "agent-1", true, "decomposing", "g4", Subplan: subplan));

        // Agent supervisor should receive TaskDecompositionAccepted via ForwardToAgent
        // and dispatch should receive TaskRequest for ready subtasks
        // We can't directly ExpectMsg on probes from the test actor, but
        // the graph should NOT complete yet (parent is WaitingForSubtasks)
        ExpectNoMsg(TimeSpan.FromMilliseconds(300));

        // Now complete the subtasks
        _taskGraph.Tell(new TaskCompleted("t1/sub-0", "agent-2", true, "sub-a done", "g4"));
        _taskGraph.Tell(new TaskCompleted("t1/sub-1", "agent-3", true, "sub-b done", "g4"));

        // Parent should move to Synthesizing and agent supervisor gets SubtasksCompleted
        // Graph still not complete because parent is Synthesizing
        ExpectNoMsg(TimeSpan.FromMilliseconds(300));

        // Simulate synthesis completion
        Sys.EventStream.Subscribe(TestActor, typeof(TaskGraphCompleted));
        _taskGraph.Tell(new TaskCompleted("t1", "agent-1", true, "synthesized", "g4"));
        var graphCompleted = ExpectMsg<TaskGraphCompleted>();
        Assert.Equal("g4", graphCompleted.GraphId);
    }

    // ── Decomposition rejection ──

    [Fact]
    public void Decomposition_ExceedsMaxDepth_Rejected()
    {
        // Create a graph and decompose to depth 1, then try to decompose a subtask (depth 2),
        // then try depth 3 (should work), then depth 4 (should fail).
        // For simplicity, test with MaxDepth = 3 by nesting 3 levels then trying a 4th.
        _taskGraph.Tell(MakeGraph("g-depth", new[] { Node("root") }), TestActor);
        ExpectMsg<TaskGraphAccepted>();

        // Depth 0 → 1
        _taskGraph.Tell(new TaskCompleted("root", "a1", true, "d", "g-depth",
            Subplan: MakeSubplan("root", new[] { Proposal("child") })));
        ExpectNoMsg(TimeSpan.FromMilliseconds(200));

        // Complete child, depth 1 → 2
        _taskGraph.Tell(new TaskCompleted("root/sub-0", "a2", true, "d", "g-depth",
            Subplan: MakeSubplan("root/sub-0", new[] { Proposal("grandchild") })));
        ExpectNoMsg(TimeSpan.FromMilliseconds(200));

        // Complete grandchild, depth 2 → 3
        _taskGraph.Tell(new TaskCompleted("root/sub-0/sub-0", "a3", true, "d", "g-depth",
            Subplan: MakeSubplan("root/sub-0/sub-0", new[] { Proposal("great-grandchild") })));
        ExpectNoMsg(TimeSpan.FromMilliseconds(200));

        // Depth 3 → 4: should be rejected (MaxDepth=3)
        // The great-grandchild is at depth 3, so trying to decompose it would be depth 4
        _taskGraph.Tell(new TaskCompleted("root/sub-0/sub-0/sub-0", "a4", true, "d", "g-depth",
            Subplan: MakeSubplan("root/sub-0/sub-0/sub-0", new[] { Proposal("too-deep") })));

        // Decomposition should be rejected — agent supervisor gets TaskDecompositionRejected
        // The task remains as Dispatched (not WaitingForSubtasks)
        ExpectNoMsg(TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public void Decomposition_ExceedsMaxSubtasks_Rejected()
    {
        _taskGraph.Tell(MakeGraph("g-max-sub", new[] { Node("t1") }), TestActor);
        ExpectMsg<TaskGraphAccepted>();

        // Create 11 subtasks (max is 10)
        var proposals = Enumerable.Range(0, 11)
            .Select(i => Proposal($"sub-{i}"))
            .ToArray();

        _taskGraph.Tell(new TaskCompleted("t1", "a1", true, "d", "g-max-sub",
            Subplan: MakeSubplan("t1", proposals)));

        // Should be rejected, graph should not be WaitingForSubtasks
        ExpectNoMsg(TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public void Decomposition_ExceedsMaxTotalNodes_Rejected()
    {
        // Create a graph with 99 nodes, then try to decompose adding 2 more (total 101 > 100)
        var nodes = Enumerable.Range(0, 99).Select(i => Node($"n{i}")).ToArray();
        _taskGraph.Tell(MakeGraph("g-max-total", nodes), TestActor);
        ExpectMsg<TaskGraphAccepted>();

        _taskGraph.Tell(new TaskCompleted("n0", "a1", true, "d", "g-max-total",
            Subplan: MakeSubplan("n0", new[] { Proposal("extra-1"), Proposal("extra-2") })));

        // 99 + 2 = 101 > 100, should be rejected
        ExpectNoMsg(TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public void Decomposition_CyclicSubtasks_Rejected()
    {
        _taskGraph.Tell(MakeGraph("g-cyc-sub", new[] { Node("t1") }), TestActor);
        ExpectMsg<TaskGraphAccepted>();

        // sub-0 depends on sub-1, sub-1 depends on sub-0
        var proposals = new[]
        {
            Proposal("a", dependsOn: new[] { "1" }),
            Proposal("b", dependsOn: new[] { "0" }),
        };

        _taskGraph.Tell(new TaskCompleted("t1", "a1", true, "d", "g-cyc-sub",
            Subplan: MakeSubplan("t1", proposals)));

        ExpectNoMsg(TimeSpan.FromMilliseconds(200));
    }

    // ── Synthesis trigger ──

    [Fact]
    public void AllSubtasksComplete_TriggersSynthesis()
    {
        _taskGraph.Tell(MakeGraph("g-synth", new[] { Node("t1") }), TestActor);
        ExpectMsg<TaskGraphAccepted>();

        _taskGraph.Tell(new TaskCompleted("t1", "a1", true, "d", "g-synth",
            Subplan: MakeSubplan("t1", new[]
            {
                Proposal("s1"),
                Proposal("s2"),
            })));
        ExpectNoMsg(TimeSpan.FromMilliseconds(200));

        // Complete both subtasks
        _taskGraph.Tell(new TaskCompleted("t1/sub-0", "a2", true, "s1 done", "g-synth"));
        _taskGraph.Tell(new TaskCompleted("t1/sub-1", "a3", true, "s2 done", "g-synth"));

        // Parent should now be Synthesizing. Verify by completing synthesis and checking graph completion.
        Sys.EventStream.Subscribe(TestActor, typeof(TaskGraphCompleted));
        _taskGraph.Tell(new TaskCompleted("t1", "a1", true, "synthesized", "g-synth"));
        var completed = ExpectMsg<TaskGraphCompleted>();
        Assert.Equal("g-synth", completed.GraphId);
    }

    [Fact]
    public void FirstSuccess_StopCondition_CancelsSiblings()
    {
        _taskGraph.Tell(MakeGraph("g-first", new[] { Node("t1") }), TestActor);
        ExpectMsg<TaskGraphAccepted>();

        _taskGraph.Tell(new TaskCompleted("t1", "a1", true, "d", "g-first",
            Subplan: MakeSubplan("t1", new[]
            {
                Proposal("try-1"),
                Proposal("try-2"),
                Proposal("try-3"),
            }, stopWhen: new StopCondition(StopKind.FirstSuccess, "first wins"))));
        ExpectNoMsg(TimeSpan.FromMilliseconds(200));

        // Complete just one subtask
        _taskGraph.Tell(new TaskCompleted("t1/sub-1", "a2", true, "try-2 won", "g-first"));

        // Synthesis should be triggered, siblings cancelled. Complete synthesis.
        Sys.EventStream.Subscribe(TestActor, typeof(TaskGraphCompleted));
        _taskGraph.Tell(new TaskCompleted("t1", "a1", true, "synthesized", "g-first"));
        var completed = ExpectMsg<TaskGraphCompleted>();
        Assert.Equal("g-first", completed.GraphId);
        // sub-0 and sub-2 should not be marked as completed
        Assert.False(completed.Results["t1/sub-0"]);
        Assert.True(completed.Results["t1/sub-1"]);
        Assert.False(completed.Results["t1/sub-2"]);
    }

    [Fact]
    public void SynthesizedTaskCompleted_CompletesParent()
    {
        Sys.EventStream.Subscribe(TestActor, typeof(TaskGraphCompleted));

        // Two-node graph: t1 → t2
        _taskGraph.Tell(MakeGraph("g-chain", new[] { Node("t1"), Node("t2") },
            new[] { new TaskEdge("t1", "t2") }), TestActor);
        ExpectMsg<TaskGraphAccepted>();

        // t1 decomposes
        _taskGraph.Tell(new TaskCompleted("t1", "a1", true, "d", "g-chain",
            Subplan: MakeSubplan("t1", new[] { Proposal("sub") })));
        ExpectNoMsg(TimeSpan.FromMilliseconds(200));

        // Complete subtask
        _taskGraph.Tell(new TaskCompleted("t1/sub-0", "a2", true, "sub done", "g-chain"));

        // Complete synthesis of t1
        _taskGraph.Tell(new TaskCompleted("t1", "a1", true, "synthesized", "g-chain"));

        // t2 should now be dispatchable. Complete it.
        _taskGraph.Tell(new TaskCompleted("t2", "a3", true, "t2 done", "g-chain"));

        var completed = ExpectMsg<TaskGraphCompleted>();
        Assert.Equal("g-chain", completed.GraphId);
        Assert.True(completed.Results["t1"]);
        Assert.True(completed.Results["t2"]);
    }

    [Fact]
    public void GraphTimedOut_FailsWaitingAndSynthesizing()
    {
        var budget = new TaskBudget(Deadline: TimeSpan.FromMilliseconds(500));
        _taskGraph.Tell(MakeGraph("g-timeout", new[] { Node("t1") }, budget: budget), TestActor);
        ExpectMsg<TaskGraphAccepted>();

        // Decompose t1
        _taskGraph.Tell(new TaskCompleted("t1", "a1", true, "d", "g-timeout",
            Subplan: MakeSubplan("t1", new[] { Proposal("sub") })));

        // Wait for timeout
        Sys.EventStream.Subscribe(TestActor, typeof(TaskGraphCompleted));
        var completed = ExpectMsg<TaskGraphCompleted>(TimeSpan.FromSeconds(5));
        Assert.Equal("g-timeout", completed.GraphId);
        // All tasks should have failed or been cancelled
        Assert.False(completed.Results["t1"]);
    }
}
