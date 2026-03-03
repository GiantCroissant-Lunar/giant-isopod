using Akka.Actor;
using Akka.TestKit.Xunit2;
using GiantIsopod.Contracts.Core;
using GiantIsopod.Plugin.Actors;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GiantIsopod.Plugin.Actors.Tests;

public class ViewportActorTests : TestKit
{
    private readonly IActorRef _viewport;
    private readonly MockViewportBridge _initialBridge;
    private readonly MockViewportBridge _replacementBridge;

    public ViewportActorTests()
    {
        _viewport = Sys.ActorOf(Props.Create(() =>
            new ViewportActor(NullLogger<ViewportActor>.Instance)));
        _initialBridge = new MockViewportBridge();
        _replacementBridge = new MockViewportBridge();

        // Set initial bridge
        _viewport.Tell(new SetViewportBridge(_initialBridge));
    }

    [Fact]
    public void ArtifactFollowupSuggested_ForwardsToBridge()
    {
        var suggestions = new[]
        {
            new ArtifactFollowupSuggestion(
                "suggestion-1",
                "artifact-1",
                "Add unit tests",
                "Create unit tests for the new feature",
                new HashSet<string> { "testing" },
                new List<string> { "project/tests" },
                new List<string> { "ArtifactTest.cs" })
        };

        _viewport.Tell(new ArtifactFollowupSuggested("artifact-1", suggestions), TestActor);

        // Wait for actor to process the message
        ExpectNoMsg(TimeSpan.FromMilliseconds(100));

        Assert.Equal("artifact-1", _initialBridge.LastArtifactFollowupSuggestedArtifactId);
        Assert.NotNull(_initialBridge.LastArtifactFollowupSuggestedJson);
        Assert.Contains("Add unit tests", _initialBridge.LastArtifactFollowupSuggestedJson);
    }

    [Fact]
    public void ArtifactFollowupSubmitted_ForwardsToBridge()
    {
        _viewport.Tell(
            new ArtifactFollowupSubmitted(
                "artifact-1",
                "graph-1",
                new[] { "task-1", "task-2" }),
            TestActor);

        // Wait for actor to process the message
        ExpectNoMsg(TimeSpan.FromMilliseconds(100));

        Assert.Equal("artifact-1", _initialBridge.LastArtifactFollowupSubmittedArtifactId);
        Assert.NotNull(_initialBridge.LastArtifactFollowupSubmittedJson);
        Assert.Contains("graph-1", _initialBridge.LastArtifactFollowupSubmittedJson);
        Assert.Contains("task-1", _initialBridge.LastArtifactFollowupSubmittedJson);
    }

    [Fact]
    public void ArtifactFollowupSuggested_AfterBridgeReplacement_UsesNewBridge()
    {
        // Send message with initial bridge
        var suggestions = new[]
        {
            new ArtifactFollowupSuggestion(
                "suggestion-1",
                "artifact-1",
                "Initial suggestion",
                "Before replacement",
                new HashSet<string> { "testing" })
        };

        _viewport.Tell(new ArtifactFollowupSuggested("artifact-1", suggestions), TestActor);
        ExpectNoMsg(TimeSpan.FromMilliseconds(100));
        Assert.Equal("artifact-1", _initialBridge.LastArtifactFollowupSuggestedArtifactId);

        // Replace bridge
        _viewport.Tell(new SetViewportBridge(_replacementBridge));
        ExpectNoMsg(TimeSpan.FromMilliseconds(100));

        // Send message with new bridge
        var newSuggestions = new[]
        {
            new ArtifactFollowupSuggestion(
                "suggestion-2",
                "artifact-2",
                "New suggestion",
                "After replacement",
                new HashSet<string> { "testing" })
        };

        _viewport.Tell(new ArtifactFollowupSuggested("artifact-2", newSuggestions), TestActor);
        ExpectNoMsg(TimeSpan.FromMilliseconds(100));

        // New bridge should receive the message
        Assert.Equal("artifact-2", _replacementBridge.LastArtifactFollowupSuggestedArtifactId);
        Assert.Contains("New suggestion", _replacementBridge.LastArtifactFollowupSuggestedJson);

        // Old bridge should not have received the new message
        Assert.Null(_replacementBridge.LastArtifactFollowupSubmittedArtifactId);
    }

    [Fact]
    public void ArtifactFollowupSubmitted_AfterBridgeReplacement_UsesNewBridge()
    {
        // Send message with initial bridge
        _viewport.Tell(
            new ArtifactFollowupSubmitted("artifact-1", "graph-1", new[] { "task-1" }),
            TestActor);
        ExpectNoMsg(TimeSpan.FromMilliseconds(100));
        Assert.Equal("artifact-1", _initialBridge.LastArtifactFollowupSubmittedArtifactId);

        // Replace bridge
        _viewport.Tell(new SetViewportBridge(_replacementBridge));
        ExpectNoMsg(TimeSpan.FromMilliseconds(100));

        // Send message with new bridge
        _viewport.Tell(
            new ArtifactFollowupSubmitted("artifact-2", "graph-2", new[] { "task-2", "task-3" }),
            TestActor);
        ExpectNoMsg(TimeSpan.FromMilliseconds(100));

        // New bridge should receive the message
        Assert.Equal("artifact-2", _replacementBridge.LastArtifactFollowupSubmittedArtifactId);
        Assert.Contains("graph-2", _replacementBridge.LastArtifactFollowupSubmittedJson);
        Assert.Contains("task-2", _replacementBridge.LastArtifactFollowupSubmittedJson);

        // Old bridge should still have the old message
        Assert.Equal("artifact-1", _initialBridge.LastArtifactFollowupSubmittedArtifactId);
    }

    [Fact]
    public void ArtifactFollowupSuggested_WithNoBridge_DoesNotThrow()
    {
        // Create a new ViewportActor without a bridge
        var noBridgeViewport = Sys.ActorOf(Props.Create(() =>
            new ViewportActor(NullLogger<ViewportActor>.Instance)));

        var suggestions = new[]
        {
            new ArtifactFollowupSuggestion(
                "suggestion-1",
                "artifact-1",
                "Test suggestion",
                "Should not throw",
                new HashSet<string> { "testing" })
        };

        // Should not throw even though no bridge is set
        noBridgeViewport.Tell(new ArtifactFollowupSuggested("artifact-1", suggestions), TestActor);

        // No exception should be raised
        ExpectNoMsg(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public void ArtifactFollowupSubmitted_WithNoBridge_DoesNotThrow()
    {
        // Create a new ViewportActor without a bridge
        var noBridgeViewport = Sys.ActorOf(Props.Create(() =>
            new ViewportActor(NullLogger<ViewportActor>.Instance)));

        // Should not throw even though no bridge is set
        noBridgeViewport.Tell(
            new ArtifactFollowupSubmitted("artifact-1", "graph-1", new[] { "task-1" }),
            TestActor);

        // No exception should be raised
        ExpectNoMsg(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public void ArtifactFollowupSuggested_SerializesSuggestionsToJson()
    {
        var suggestions = new[]
        {
            new ArtifactFollowupSuggestion(
                "suggestion-1",
                "artifact-1",
                "Add tests",
                "Create unit tests",
                new HashSet<string> { "testing", "code_edit" },
                new List<string> { "project/tests" },
                new List<string> { "TestFile.cs" },
                new List<string> { "validator-1" },
                "runtime-1"),
            new ArtifactFollowupSuggestion(
                "suggestion-2",
                "artifact-1",
                "Add docs",
                "Create documentation",
                new HashSet<string> { "documentation" })
        };

        _viewport.Tell(new ArtifactFollowupSuggested("artifact-1", suggestions), TestActor);
        ExpectNoMsg(TimeSpan.FromMilliseconds(100));

        Assert.NotNull(_initialBridge.LastArtifactFollowupSuggestedJson);
        Assert.Contains("suggestion-1", _initialBridge.LastArtifactFollowupSuggestedJson);
        Assert.Contains("Add tests", _initialBridge.LastArtifactFollowupSuggestedJson);
        Assert.Contains("suggestion-2", _initialBridge.LastArtifactFollowupSuggestedJson);
        Assert.Contains("Add docs", _initialBridge.LastArtifactFollowupSuggestedJson);
        Assert.Contains("testing", _initialBridge.LastArtifactFollowupSuggestedJson);
        Assert.Contains("code_edit", _initialBridge.LastArtifactFollowupSuggestedJson);
    }

    [Fact]
    public void ArtifactFollowupSubmitted_SerializesSubmissionToJson()
    {
        _viewport.Tell(
            new ArtifactFollowupSubmitted(
                "artifact-1",
                "graph-123",
                new[] { "task-a", "task-b", "task-c" }),
            TestActor);
        ExpectNoMsg(TimeSpan.FromMilliseconds(100));

        Assert.NotNull(_initialBridge.LastArtifactFollowupSubmittedJson);
        Assert.Contains("graph-123", _initialBridge.LastArtifactFollowupSubmittedJson);
        Assert.Contains("task-a", _initialBridge.LastArtifactFollowupSubmittedJson);
        Assert.Contains("task-b", _initialBridge.LastArtifactFollowupSubmittedJson);
        Assert.Contains("task-c", _initialBridge.LastArtifactFollowupSubmittedJson);
    }

    private class MockViewportBridge : IViewportBridge
    {
        public string? LastArtifactFollowupSuggestedArtifactId { get; private set; }
        public string? LastArtifactFollowupSuggestedJson { get; private set; }
        public string? LastArtifactFollowupSubmittedArtifactId { get; private set; }
        public string? LastArtifactFollowupSubmittedJson { get; private set; }

        public void PublishAgentStateChanged(string agentId, AgentActivityState state)
        {
        }

        public void PublishAgentSpawned(string agentId, AgentVisualInfo visualInfo)
        {
        }

        public void PublishAgentDespawned(string agentId)
        {
        }

        public void PublishGenUIRequest(string agentId, string a2uiJson)
        {
        }

        public void PublishRuntimeStarted(string agentId, int processId)
        {
        }

        public void PublishRuntimeExited(string agentId, int exitCode)
        {
        }

        public void PublishRuntimeOutput(string agentId, string line)
        {
        }

        public void PublishAgUiEvent(string agentId, object agUiEvent)
        {
        }

        public void PublishTaskGraphSubmitted(string graphId, IReadOnlyList<TaskNode> nodes, IReadOnlyList<TaskEdge> edges)
        {
        }

        public void PublishTaskNodeStatusChanged(string graphId, string taskId, TaskNodeStatus status, string? agentId = null)
        {
        }

        public void PublishTaskGraphCompleted(string graphId, IReadOnlyDictionary<string, bool> results)
        {
        }

        public void PublishArtifactFollowUpSuggested(string agentId, string artifactId, string suggestion)
        {
            LastArtifactFollowupSuggestedArtifactId = artifactId;
            LastArtifactFollowupSuggestedJson = suggestion;
        }

        public void PublishArtifactFollowUpSubmitted(string agentId, string artifactId, string submission)
        {
            LastArtifactFollowupSubmittedArtifactId = artifactId;
            LastArtifactFollowupSubmittedJson = submission;
        }
    }
}
