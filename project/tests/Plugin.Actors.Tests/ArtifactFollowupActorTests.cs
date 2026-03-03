using Akka.Actor;
using Akka.TestKit.Xunit2;
using GiantIsopod.Contracts.Core;
using GiantIsopod.Plugin.Actors;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GiantIsopod.Plugin.Actors.Tests;

public class ArtifactFollowupActorTests : TestKit
{
    private readonly Akka.TestKit.TestProbe _taskGraphProbe;
    private readonly IActorRef _artifactRegistry;
    private readonly IActorRef _followups;

    public ArtifactFollowupActorTests()
    {
        _taskGraphProbe = CreateTestProbe();
        _artifactRegistry = Sys.ActorOf(Props.Create(() =>
            new ArtifactRegistryActor(NullLogger<ArtifactRegistryActor>.Instance)));
        _followups = Sys.ActorOf(Props.Create(() =>
            new ArtifactFollowupActor(
                _artifactRegistry,
                _taskGraphProbe.Ref,
                NullLogger<ArtifactFollowupActor>.Instance)));
    }

    private static ArtifactRef MakeCodeArtifact(string artifactId, string relativePath) =>
        new(
            artifactId,
            ArtifactType.Code,
            "text/plain",
            Path.Combine(Path.GetTempPath(), $"{artifactId}.cs"),
            null,
            new ArtifactProvenance("task-1", "agent-1", DateTimeOffset.UtcNow),
            new Dictionary<string, string> { ["relativePath"] = relativePath });

    [Fact]
    public void BlessedCodeArtifact_PublishesFollowupSuggestions()
    {
        Sys.EventStream.Subscribe(TestActor, typeof(ArtifactFollowupSuggested));

        var artifact = MakeCodeArtifact("art-1", "project/plugins/Plugin.Actors/DispatchActor.cs");
        _artifactRegistry.Tell(new RegisterArtifact(artifact), TestActor);
        ExpectMsg<ArtifactRegistered>();

        _artifactRegistry.Tell(new BlessArtifact("art-1"), TestActor);
        ExpectMsg<ArtifactBlessed>();

        var suggested = ExpectMsg<ArtifactFollowupSuggested>(TimeSpan.FromSeconds(5));
        Assert.Equal("art-1", suggested.ArtifactId);
        Assert.Contains(suggested.Suggestions, s => s.SuggestionId == "art-1:tests");
        Assert.Contains(suggested.Suggestions, s => s.SuggestionId == "art-1:docs");
    }

    [Fact]
    public void SubmitArtifactFollowup_SelectedSuggestion_SubmitsTaskGraph()
    {
        var artifact = MakeCodeArtifact("art-2", "project/plugins/Plugin.Actors/DispatchActor.cs");
        _artifactRegistry.Tell(new RegisterArtifact(artifact), TestActor);
        ExpectMsg<ArtifactRegistered>();

        _artifactRegistry.Tell(new BlessArtifact("art-2"), TestActor);
        ExpectMsg<ArtifactBlessed>();

        _followups.Tell(new GetArtifactFollowups("art-2"), TestActor);
        var suggestions = ExpectMsg<ArtifactFollowupResult>(TimeSpan.FromSeconds(5));
        Assert.Contains(suggestions.Suggestions, s => s.SuggestionId == "art-2:tests");

        _followups.Tell(
            new SubmitArtifactFollowup("art-2", new[] { "art-2:tests" }, "artifact-followup-test"),
            TestActor);

        var submitted = ExpectMsg<ArtifactFollowupSubmitted>(TimeSpan.FromSeconds(5));
        Assert.Equal("artifact-followup-test", submitted.GraphId);
        Assert.Single(submitted.TaskIds);
        Assert.Equal("art-2:tests", submitted.TaskIds[0]);

        var graph = _taskGraphProbe.ExpectMsg<SubmitTaskGraph>(TimeSpan.FromSeconds(5));
        Assert.Equal("artifact-followup-test", graph.GraphId);
        Assert.Single(graph.Nodes);
        Assert.Equal("art-2:tests", graph.Nodes[0].TaskId);
        Assert.Contains("project/tests", graph.Nodes[0].OwnedPaths ?? Array.Empty<string>());
        Assert.Contains("testing", graph.Nodes[0].RequiredCapabilities);
    }
}
