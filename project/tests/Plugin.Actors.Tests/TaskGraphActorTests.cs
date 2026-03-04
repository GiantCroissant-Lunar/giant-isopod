using Akka.Actor;
using Akka.TestKit.Xunit2;
using GiantIsopod.Contracts.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GiantIsopod.Plugin.Actors.Tests;

public class TaskGraphActorTests : TestKit
{
    private readonly Akka.TestKit.TestProbe _dispatchProbe;
    private readonly Akka.TestKit.TestProbe _agentSupervisorProbe;
    private readonly Akka.TestKit.TestProbe _viewportProbe;
    private readonly Akka.TestKit.TestProbe _workspaceProbe;
    private readonly Akka.TestKit.TestProbe _validatorProbe;
    private readonly IActorRef _taskGraph;

    public TaskGraphActorTests()
    {
        _dispatchProbe = CreateTestProbe();
        _agentSupervisorProbe = CreateTestProbe();
        _viewportProbe = CreateTestProbe();
        _workspaceProbe = CreateTestProbe();
        _validatorProbe = CreateTestProbe();

        _taskGraph = Sys.ActorOf(Props.Create(() =>
            new TaskGraphActor(
                _dispatchProbe.Ref,
                _agentSupervisorProbe.Ref,
                _viewportProbe.Ref,
                _workspaceProbe.Ref,
                _validatorProbe.Ref,
                NullLogger<TaskGraphActor>.Instance)));
    }

    private IActorRef CreateTaskGraph(ITaskGraphCheckpointStore checkpointStore, IActorRef? knowledgeSupervisor = null)
    {
        return Sys.ActorOf(Props.Create(() =>
            new TaskGraphActor(
                _dispatchProbe.Ref,
                _agentSupervisorProbe.Ref,
                _viewportProbe.Ref,
                _workspaceProbe.Ref,
                _validatorProbe.Ref,
                knowledgeSupervisor ?? ActorRefs.Nobody,
                checkpointStore,
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

    private static TaskNode NodeWithPaths(
        string id,
        string desc,
        IReadOnlyList<string> ownedPaths,
        IReadOnlyList<string>? expectedFiles = null,
        params string[] caps) =>
        new(
            id,
            desc,
            new HashSet<string>(caps),
            OwnedPaths: ownedPaths,
            ExpectedFiles: expectedFiles);

    private static ProposedSubplan MakeSubplan(
        string parentTaskId,
        IReadOnlyList<SubtaskProposal> subtasks,
        DecompositionReason reason = DecompositionReason.TooLarge,
        StopCondition? stopWhen = null) =>
        new(parentTaskId, reason, subtasks, stopWhen);

    private static SubtaskProposal Proposal(
        string desc = "subtask",
        IReadOnlyList<string>? dependsOn = null,
        IReadOnlyList<string>? ownedPaths = null,
        IReadOnlyList<string>? expectedFiles = null,
        params string[] caps) =>
        new(
            desc,
            new HashSet<string>(caps),
            dependsOn ?? Array.Empty<string>(),
            OwnedPaths: ownedPaths ?? new[] { $"project/tasks/{desc.Replace(' ', '-').ToLowerInvariant()}.txt" },
            ExpectedFiles: expectedFiles ?? new[] { $"project/tasks/{desc.Replace(' ', '-').ToLowerInvariant()}.txt" });

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

    [Fact]
    public void Submit_OverlappingOwnedPaths_AutoSequencesTasks()
    {
        Sys.EventStream.Subscribe(TestActor, typeof(TaskGraphCompleted));

        var submit = MakeGraph(
            "g-owned-paths",
            new[]
            {
                NodeWithPaths("t1", "edit dispatch logic", new[] { "project/plugins/Plugin.Actors/DispatchActor.cs" }, caps: "code_edit"),
                NodeWithPaths("t2", "edit dispatch tests", new[] { "project/plugins/Plugin.Actors" }, caps: "code_edit")
            });

        _taskGraph.Tell(submit, TestActor);
        ExpectMsg<TaskGraphAccepted>();

        var firstDispatch = _dispatchProbe.ExpectMsg<TaskRequest>();
        Assert.Equal("t1", firstDispatch.TaskId);
        _dispatchProbe.ExpectNoMsg(TimeSpan.FromMilliseconds(200));

        _taskGraph.Tell(new TaskCompleted("t1", "agent-1", true, "done", "g-owned-paths"));
        var releaseT1 = _workspaceProbe.ExpectMsg<ReleaseWorkspace>(TimeSpan.FromSeconds(5));
        Assert.Equal("t1", releaseT1.TaskId);
        _taskGraph.Tell(new WorkspaceReleased("t1"));

        var secondDispatch = _dispatchProbe.ExpectMsg<TaskRequest>(TimeSpan.FromSeconds(5));
        Assert.Equal("t2", secondDispatch.TaskId);

        _taskGraph.Tell(new TaskCompleted("t2", "agent-2", true, "done", "g-owned-paths"));
        var releaseT2 = _workspaceProbe.ExpectMsg<ReleaseWorkspace>(TimeSpan.FromSeconds(5));
        Assert.Equal("t2", releaseT2.TaskId);
        _taskGraph.Tell(new WorkspaceReleased("t2"));

        var completed = ExpectMsg<TaskGraphCompleted>(TimeSpan.FromSeconds(5));
        Assert.True(completed.Results["t1"]);
        Assert.True(completed.Results["t2"]);
    }

    // ── Task completion without decomposition ──

    [Fact]
    public void TaskCompleted_NoSubplan_CompletesGraph()
    {
        Sys.EventStream.Subscribe(TestActor, typeof(TaskGraphCompleted));

        _taskGraph.Tell(MakeGraph("g3", new[] { Node("t1") }), TestActor);
        ExpectMsg<TaskGraphAccepted>();

        _taskGraph.Tell(new TaskCompleted("t1", "agent-1", true, "done", "g3"));
        var release = _workspaceProbe.ExpectMsg<ReleaseWorkspace>(TimeSpan.FromSeconds(5));
        Assert.Equal("t1", release.TaskId);
        _taskGraph.Tell(new WorkspaceReleased("t1"));

        var completed = ExpectMsg<TaskGraphCompleted>(TimeSpan.FromSeconds(5));
        Assert.Equal("g3", completed.GraphId);
        Assert.True(completed.Results["t1"]);
    }

    [Fact]
    public void PlannerEnabledTask_DispatchesPlannerBeforeImplementation()
    {
        var node = new TaskNode(
            "t-plan",
            "complex task",
            new HashSet<string> { "code_edit" },
            PlannerRequiredCapabilities: new HashSet<string> { "task_decompose" },
            PreferredPlannerRuntimeId: "claude-code");

        _taskGraph.Tell(MakeGraph("g-plan-first", new[] { node }), TestActor);
        ExpectMsg<TaskGraphAccepted>();

        var plannerDispatch = _dispatchProbe.ExpectMsg<TaskRequest>(TimeSpan.FromSeconds(5));
        Assert.Equal("t-plan.__plan", plannerDispatch.TaskId);
        Assert.Equal("claude-code", plannerDispatch.PreferredRuntimeId);
        Assert.Contains("task_decompose", plannerDispatch.RequiredCapabilities);

        _taskGraph.Tell(new TaskCompleted("t-plan.__plan", "planner-1", true, "execute directly", "g-plan-first"));

        var implementationDispatch = _dispatchProbe.ExpectMsg<TaskRequest>(TimeSpan.FromSeconds(5));
        Assert.Equal("t-plan", implementationDispatch.TaskId);
        Assert.Contains("code_edit", implementationDispatch.RequiredCapabilities);
    }

    [Fact]
    public void PlannerAssignment_UpdatesParentPlanningStatusWithAgent()
    {
        var node = new TaskNode(
            "t-plan",
            "complex task",
            new HashSet<string> { "code_edit" },
            PlannerRequiredCapabilities: new HashSet<string> { "task_decompose" },
            PreferredPlannerRuntimeId: "claude-code");

        _taskGraph.Tell(MakeGraph("g-plan-assign", new[] { node }), TestActor);
        ExpectMsg<TaskGraphAccepted>();

        _dispatchProbe.ExpectMsg<TaskRequest>(msg => msg.TaskId == "t-plan.__plan", TimeSpan.FromSeconds(5));

        _taskGraph.Tell(new TaskAssigned("t-plan.__plan", "planner-1", GraphId: "g-plan-assign"));

        _viewportProbe.FishForMessage(
            msg => msg is NotifyTaskNodeStatusChanged changed
                   && changed.GraphId == "g-plan-assign"
                   && changed.TaskId == "t-plan"
                   && changed.Status == TaskNodeStatus.Planning
                   && changed.AgentId == "planner-1",
            TimeSpan.FromSeconds(5),
            "Expected planning status update with assigned planner agent.");
    }

    [Fact]
    public void TaskAssignment_UpdatesDispatchedStatusWithAgent()
    {
        _taskGraph.Tell(MakeGraph("g-assign", new[] { Node("t1", caps: "code_edit") }), TestActor);
        ExpectMsg<TaskGraphAccepted>();

        _dispatchProbe.ExpectMsg<TaskRequest>(msg => msg.TaskId == "t1", TimeSpan.FromSeconds(5));

        _taskGraph.Tell(new TaskAssigned("t1", "agent-1", GraphId: "g-assign"));

        _viewportProbe.FishForMessage(
            msg => msg is NotifyTaskNodeStatusChanged changed
                   && changed.GraphId == "g-assign"
                   && changed.TaskId == "t1"
                   && changed.Status == TaskNodeStatus.Dispatched
                   && changed.AgentId == "agent-1",
            TimeSpan.FromSeconds(5),
            "Expected dispatched status update with assigned executor agent.");
    }

    [Fact]
    public void PlannerEnabledTask_SubplanFromPlannerInsertsSubtasks()
    {
        _taskGraph.Tell(MakeGraph("g-plan-sub", new[]
        {
            new TaskNode(
                "t1",
                "decompose me",
                new HashSet<string> { "code_edit" },
                PlannerRequiredCapabilities: new HashSet<string> { "task_decompose" },
                PreferredPlannerRuntimeId: "claude-code")
        }), TestActor);
        ExpectMsg<TaskGraphAccepted>();

        _dispatchProbe.ExpectMsg<TaskRequest>(msg => msg.TaskId == "t1.__plan", TimeSpan.FromSeconds(5));

        var subplan = MakeSubplan("t1", new[]
        {
            Proposal("sub-a", caps: "cap-1"),
            Proposal("sub-b", dependsOn: new[] { "0" }, caps: "cap-2"),
        });

        _taskGraph.Tell(new TaskCompleted("t1.__plan", "planner-1", true, "decompose", "g-plan-sub", Subplan: subplan));

        var subtaskRequest = _dispatchProbe.ExpectMsg<TaskRequest>(msg => msg.TaskId == "t1/sub-0", TimeSpan.FromSeconds(5));
        Assert.Contains("cap-1", subtaskRequest.RequiredCapabilities);
        _dispatchProbe.ExpectNoMsg(TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public void PlannerEnabledTask_QueriesPlanningKnowledgeBeforeDispatch()
    {
        var knowledgeProbe = CreateTestProbe();
        var taskGraph = CreateTaskGraph(NullTaskGraphCheckpointStore.Instance, knowledgeProbe.Ref);
        var now = DateTimeOffset.UtcNow;

        taskGraph.Tell(MakeGraph("g-plan-knowledge", new[]
        {
            new TaskNode(
                "t1",
                "Refactor executable resolution for isolated worktrees.",
                new HashSet<string> { "code_edit" },
                PlannerRequiredCapabilities: new HashSet<string> { "task_decompose" },
                PreferredPlannerRuntimeId: "claude-code",
                OwnedPaths: new[] { "project/plugins/Plugin.Process" },
                ExpectedFiles: new[] { "project/plugins/Plugin.Process/CliExecutableResolver.cs" })
        }), TestActor);
        ExpectMsg<TaskGraphAccepted>();

        var query = knowledgeProbe.ExpectMsg<QueryKnowledge>(TimeSpan.FromSeconds(5));
        Assert.Equal("task-planner", query.AgentId);
        Assert.Equal("planning-pitfall", query.Category);
        Assert.Contains("CliExecutableResolver.cs", query.Query);

        _dispatchProbe.ExpectNoMsg(TimeSpan.FromMilliseconds(200));

        knowledgeProbe.Reply(new KnowledgeResult(
            "task-planner",
            new[]
            {
                new KnowledgeEntry(
                    "Do not split resolver and sidecar executable discovery into overlapping file clusters.",
                    "planning-pitfall",
                    0.91,
                    new Dictionary<string, string> { ["kind"] = "merge_conflict" },
                    now)
            }));

        var plannerDispatch = _dispatchProbe.ExpectMsg<TaskRequest>(TimeSpan.FromSeconds(5));
        Assert.Equal("t1.__plan", plannerDispatch.TaskId);
        Assert.Contains("Planning feedback context:", plannerDispatch.Description);
        Assert.Contains("Do not split resolver and sidecar executable discovery", plannerDispatch.Description);
    }

    [Fact]
    public void PlannerSubtask_UpdateExistingFile_DefaultsToAllowNoOpCompletion()
    {
        _taskGraph.Tell(MakeGraph("g-plan-noop", new[]
        {
            new TaskNode(
                "t1",
                "decompose docs",
                new HashSet<string> { "code_edit" },
                PlannerRequiredCapabilities: new HashSet<string> { "task_decompose" },
                PreferredPlannerRuntimeId: "claude-code")
        }), TestActor);
        ExpectMsg<TaskGraphAccepted>();

        _dispatchProbe.ExpectMsg<TaskRequest>(msg => msg.TaskId == "t1.__plan", TimeSpan.FromSeconds(5));

        var subplan = MakeSubplan("t1", new[]
        {
            new SubtaskProposal(
                "Update docs/decisions/010-agent-middleware-pipeline.md to record the A2A schema decision.",
                new HashSet<string> { "code_edit" },
                Array.Empty<string>(),
                OwnedPaths: new[] { "docs/decisions/010-agent-middleware-pipeline.md" },
                ExpectedFiles: new[] { "docs/decisions/010-agent-middleware-pipeline.md" },
                AllowNoOpCompletion: false)
        });

        _taskGraph.Tell(new TaskCompleted("t1.__plan", "planner-1", true, "decompose", "g-plan-noop", Subplan: subplan));

        var subtaskRequest = _dispatchProbe.ExpectMsg<TaskRequest>(msg => msg.TaskId == "t1/sub-0", TimeSpan.FromSeconds(5));
        Assert.True(subtaskRequest.AllowNoOpCompletion);
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
        var releaseSub0 = _workspaceProbe.ExpectMsg<ReleaseWorkspace>(TimeSpan.FromSeconds(5));
        Assert.Equal("t1/sub-0", releaseSub0.TaskId);
        _taskGraph.Tell(new WorkspaceReleased("t1/sub-0"));

        _taskGraph.Tell(new TaskCompleted("t1/sub-1", "agent-3", true, "sub-b done", "g4"));
        var releaseSub1 = _workspaceProbe.ExpectMsg<ReleaseWorkspace>(TimeSpan.FromSeconds(5));
        Assert.Equal("t1/sub-1", releaseSub1.TaskId);
        _taskGraph.Tell(new WorkspaceReleased("t1/sub-1"));

        // Parent should move to Synthesizing and agent supervisor gets SubtasksCompleted
        // Graph still not complete because parent is Synthesizing
        ExpectNoMsg(TimeSpan.FromMilliseconds(300));

        // Simulate synthesis completion
        Sys.EventStream.Subscribe(TestActor, typeof(TaskGraphCompleted));
        _taskGraph.Tell(new TaskCompleted("t1", "agent-1", true, "synthesized", "g4"));
        var release = _workspaceProbe.ExpectMsg<ReleaseWorkspace>(TimeSpan.FromSeconds(5));
        Assert.Equal("t1", release.TaskId);
        _taskGraph.Tell(new WorkspaceReleased("t1"));
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

    [Fact]
    public void Decomposition_SubtaskMissingOwnedPaths_IsRejected()
    {
        _taskGraph.Tell(MakeGraph("g-missing-owned", new[] { Node("t1") }), TestActor);
        ExpectMsg<TaskGraphAccepted>();
        _dispatchProbe.ExpectMsg<TaskRequest>(TimeSpan.FromSeconds(5));

        var proposals = new[]
        {
            new SubtaskProposal(
                "subtask",
                new HashSet<string> { "analysis" },
                Array.Empty<string>(),
                OwnedPaths: Array.Empty<string>(),
                ExpectedFiles: new[] { "project/docs/subtask.md" })
        };

        _taskGraph.Tell(new TaskCompleted("t1", "a1", true, "d", "g-missing-owned",
            Subplan: MakeSubplan("t1", proposals)));

        ExpectNoMsg(TimeSpan.FromMilliseconds(200));
        _dispatchProbe.ExpectNoMsg(TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public void TaskFailed_StoresPlanningPitfallKnowledge()
    {
        var knowledgeProbe = CreateTestProbe();
        var taskGraph = Sys.ActorOf(Props.Create(() =>
            new TaskGraphActor(
                _dispatchProbe.Ref,
                _agentSupervisorProbe.Ref,
                _viewportProbe.Ref,
                _workspaceProbe.Ref,
                _validatorProbe.Ref,
                knowledgeProbe.Ref,
                NullLogger<TaskGraphActor>.Instance)));

        var node = new TaskNode(
            "t1",
            "test task",
            new HashSet<string> { "code_edit" },
            OwnedPaths: new[] { "project/plugins/Plugin.Actors/DispatchActor.cs" },
            ExpectedFiles: new[] { "project/plugins/Plugin.Actors/DispatchActor.cs" });

        taskGraph.Tell(MakeGraph("g-learning", new[] { node }), TestActor);
        ExpectMsg<TaskGraphAccepted>();
        _dispatchProbe.ExpectMsg<TaskRequest>(TimeSpan.FromSeconds(5));

        taskGraph.Tell(new TaskFailed(
            "t1",
            "Runtime declared expected artifacts but no workspace changes were detected.",
            GraphId: "g-learning"));

        var stored = knowledgeProbe.ExpectMsg<StoreKnowledge>(TimeSpan.FromSeconds(5));
        Assert.Equal("task-planner", stored.AgentId);
        Assert.Equal("planning-pitfall", stored.Category);
        Assert.Contains("t1", stored.Content, StringComparison.Ordinal);
        Assert.Contains("DispatchActor.cs", stored.Content, StringComparison.Ordinal);
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
        var releaseSub0 = _workspaceProbe.ExpectMsg<ReleaseWorkspace>(TimeSpan.FromSeconds(5));
        Assert.Equal("t1/sub-0", releaseSub0.TaskId);
        _taskGraph.Tell(new WorkspaceReleased("t1/sub-0"));

        _taskGraph.Tell(new TaskCompleted("t1/sub-1", "a3", true, "s2 done", "g-synth"));
        var releaseSub1 = _workspaceProbe.ExpectMsg<ReleaseWorkspace>(TimeSpan.FromSeconds(5));
        Assert.Equal("t1/sub-1", releaseSub1.TaskId);
        _taskGraph.Tell(new WorkspaceReleased("t1/sub-1"));

        // Parent should now be Synthesizing. Verify by completing synthesis and checking graph completion.
        Sys.EventStream.Subscribe(TestActor, typeof(TaskGraphCompleted));
        _taskGraph.Tell(new TaskCompleted("t1", "a1", true, "synthesized", "g-synth"));
        var release = _workspaceProbe.ExpectMsg<ReleaseWorkspace>(TimeSpan.FromSeconds(5));
        Assert.Equal("t1", release.TaskId);
        _taskGraph.Tell(new WorkspaceReleased("t1"));
        var completed = ExpectMsg<TaskGraphCompleted>();
        Assert.Equal("g-synth", completed.GraphId);
    }

    [Fact]
    public void AllSubtasksComplete_WithFailedChild_FailsParentAndCompletesGraph()
    {
        Sys.EventStream.Subscribe(TestActor, typeof(TaskGraphCompleted));

        _taskGraph.Tell(MakeGraph("g-subfail", new[] { Node("t1") }), TestActor);
        ExpectMsg<TaskGraphAccepted>();

        _taskGraph.Tell(new TaskCompleted("t1", "a1", true, "d", "g-subfail",
            Subplan: MakeSubplan("t1", new[]
            {
                Proposal("s1"),
                Proposal("s2"),
            })));

        _taskGraph.Tell(new TaskCompleted("t1/sub-0", "a2", true, "s1 done", "g-subfail"));
        var releaseSub0 = _workspaceProbe.ExpectMsg<ReleaseWorkspace>(TimeSpan.FromSeconds(5));
        Assert.Equal("t1/sub-0", releaseSub0.TaskId);
        _taskGraph.Tell(new WorkspaceReleased("t1/sub-0"));

        _taskGraph.Tell(new TaskFailed("t1/sub-1", "validator failed", GraphId: "g-subfail"));
        var releaseSub1 = _workspaceProbe.ExpectMsg<ReleaseWorkspace>(TimeSpan.FromSeconds(5));
        Assert.Equal("t1/sub-1", releaseSub1.TaskId);
        _taskGraph.Tell(new WorkspaceReleased("t1/sub-1"));

        var releaseParent = _workspaceProbe.ExpectMsg<ReleaseWorkspace>(TimeSpan.FromSeconds(5));
        Assert.Equal("t1", releaseParent.TaskId);
        _taskGraph.Tell(new WorkspaceReleased("t1"));

        var completed = ExpectMsg<TaskGraphCompleted>(TimeSpan.FromSeconds(5));
        Assert.Equal("g-subfail", completed.GraphId);
        Assert.False(completed.Results["t1"]);
        Assert.True(completed.Results["t1/sub-0"]);
        Assert.False(completed.Results["t1/sub-1"]);
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
        var releaseWinner = _workspaceProbe.ExpectMsg<ReleaseWorkspace>(TimeSpan.FromSeconds(5));
        Assert.Equal("t1/sub-1", releaseWinner.TaskId);
        _taskGraph.Tell(new WorkspaceReleased("t1/sub-1"));

        // Synthesis should be triggered, siblings cancelled. Complete synthesis.
        Sys.EventStream.Subscribe(TestActor, typeof(TaskGraphCompleted));
        _taskGraph.Tell(new TaskCompleted("t1", "a1", true, "synthesized", "g-first"));
        var release = _workspaceProbe.ExpectMsg<ReleaseWorkspace>(TimeSpan.FromSeconds(5));
        Assert.Equal("t1", release.TaskId);
        _taskGraph.Tell(new WorkspaceReleased("t1"));
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
        var releaseSub0 = _workspaceProbe.ExpectMsg<ReleaseWorkspace>(TimeSpan.FromSeconds(5));
        Assert.Equal("t1/sub-0", releaseSub0.TaskId);
        _taskGraph.Tell(new WorkspaceReleased("t1/sub-0"));

        // Complete synthesis of t1
        _taskGraph.Tell(new TaskCompleted("t1", "a1", true, "synthesized", "g-chain"));
        var releaseT1 = _workspaceProbe.ExpectMsg<ReleaseWorkspace>(TimeSpan.FromSeconds(5));
        Assert.Equal("t1", releaseT1.TaskId);
        _taskGraph.Tell(new WorkspaceReleased("t1"));

        // t2 should now be dispatchable. Complete it.
        _taskGraph.Tell(new TaskCompleted("t2", "a3", true, "t2 done", "g-chain"));
        var releaseT2 = _workspaceProbe.ExpectMsg<ReleaseWorkspace>(TimeSpan.FromSeconds(5));
        Assert.Equal("t2", releaseT2.TaskId);
        _taskGraph.Tell(new WorkspaceReleased("t2"));

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

    [Fact]
    public void Submit_PersistsCheckpoint_AndCompletionDeletesIt()
    {
        var checkpointDir = Path.Combine(Path.GetTempPath(), $"gi-taskgraph-checkpoint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(checkpointDir);
        var checkpointStore = new FileTaskGraphCheckpointStore(checkpointDir);
        var taskGraph = CreateTaskGraph(checkpointStore);

        taskGraph.Tell(MakeGraph("g-checkpoint", new[] { Node("t1") }), TestActor);
        ExpectMsg<TaskGraphAccepted>();

        var saved = checkpointStore.LoadAll();
        Assert.Single(saved);
        Assert.Equal("g-checkpoint", saved[0].GraphId);

        taskGraph.Tell(new TaskCompleted("t1", "agent-1", true, "done", "g-checkpoint"));
        var release = _workspaceProbe.ExpectMsg<ReleaseWorkspace>(TimeSpan.FromSeconds(5));
        Assert.Equal("t1", release.TaskId);
        taskGraph.Tell(new WorkspaceReleased("t1"));

        Sys.EventStream.Subscribe(TestActor, typeof(TaskGraphCompleted));
        ExpectMsg<TaskGraphCompleted>(TimeSpan.FromSeconds(5));

        Assert.Empty(checkpointStore.LoadAll());
        Directory.Delete(checkpointDir, recursive: true);
    }

    [Fact]
    public void Restore_RequeuesDispatchedLeafTaskFromCheckpoint()
    {
        var checkpointDir = Path.Combine(Path.GetTempPath(), $"gi-taskgraph-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(checkpointDir);
        var checkpointStore = new FileTaskGraphCheckpointStore(checkpointDir);

        var firstActor = CreateTaskGraph(checkpointStore);
        firstActor.Tell(MakeGraph("g-restore", new[] { Node("t1") }), TestActor);
        ExpectMsg<TaskGraphAccepted>();
        _dispatchProbe.ExpectMsg<TaskRequest>(TimeSpan.FromSeconds(5));

        Watch(firstActor);
        Sys.Stop(firstActor);
        ExpectTerminated(firstActor, TimeSpan.FromSeconds(5));

        var restoredActor = CreateTaskGraph(checkpointStore);
        _dispatchProbe.ExpectMsg<TaskRequest>(msg => msg.TaskId == "t1", TimeSpan.FromSeconds(5));

        Watch(restoredActor);
        Sys.Stop(restoredActor);
        ExpectTerminated(restoredActor, TimeSpan.FromSeconds(5));
        Directory.Delete(checkpointDir, recursive: true);
    }
}
