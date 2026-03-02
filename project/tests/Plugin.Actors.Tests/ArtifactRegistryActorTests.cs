using Akka.Actor;
using Akka.TestKit.Xunit2;
using GiantIsopod.Contracts.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GiantIsopod.Plugin.Actors.Tests;

public class ArtifactRegistryActorTests : TestKit
{
    private readonly IActorRef _registry;

    public ArtifactRegistryActorTests()
    {
        _registry = Sys.ActorOf(Props.Create(() =>
            new ArtifactRegistryActor(NullLogger<ArtifactRegistryActor>.Instance)));
    }

    private static ArtifactRef MakeArtifact(
        string id = "art-001",
        ArtifactType type = ArtifactType.Code,
        string format = "cs",
        string uri = "file:src/Foo.cs",
        string? hash = "abc123",
        string taskId = "task-001",
        string agentId = "agent-001") =>
        new(id, type, format, uri, hash,
            new ArtifactProvenance(taskId, agentId, DateTimeOffset.UtcNow));

    [Fact]
    public void Register_ReturnsRegisteredId()
    {
        var art = MakeArtifact();
        _registry.Tell(new RegisterArtifact(art), TestActor);

        var result = ExpectMsg<ArtifactRegistered>();
        Assert.Equal("art-001", result.ArtifactId);
    }

    [Fact]
    public void GetArtifact_ReturnsRegisteredArtifact()
    {
        var art = MakeArtifact();
        _registry.Tell(new RegisterArtifact(art), TestActor);
        ExpectMsg<ArtifactRegistered>();

        _registry.Tell(new GetArtifact("art-001"), TestActor);
        var result = ExpectMsg<ArtifactResult>();
        Assert.NotNull(result.Artifact);
        Assert.Equal("art-001", result.Artifact!.ArtifactId);
        Assert.Equal(ArtifactType.Code, result.Artifact.Type);
        Assert.Equal("cs", result.Artifact.Format);
    }

    [Fact]
    public void GetArtifact_UnknownId_ReturnsNull()
    {
        _registry.Tell(new GetArtifact("nonexistent"), TestActor);
        var result = ExpectMsg<ArtifactResult>();
        Assert.Null(result.Artifact);
    }

    [Fact]
    public void GetByTask_ReturnsArtifactsForTask()
    {
        _registry.Tell(new RegisterArtifact(MakeArtifact("art-001", taskId: "task-A")), TestActor);
        ExpectMsg<ArtifactRegistered>();

        _registry.Tell(new RegisterArtifact(MakeArtifact("art-002", taskId: "task-A", hash: "def456")), TestActor);
        ExpectMsg<ArtifactRegistered>();

        _registry.Tell(new RegisterArtifact(MakeArtifact("art-003", taskId: "task-B", hash: "ghi789")), TestActor);
        ExpectMsg<ArtifactRegistered>();

        _registry.Tell(new GetArtifactsByTask("task-A"), TestActor);
        var result = ExpectMsg<ArtifactListResult>();
        Assert.Equal(2, result.Artifacts.Count);
        Assert.All(result.Artifacts, a => Assert.Equal("task-A", a.Provenance.TaskId));
    }

    [Fact]
    public void GetByType_ReturnsArtifactsOfType()
    {
        _registry.Tell(new RegisterArtifact(MakeArtifact("art-001", type: ArtifactType.Code, hash: "h1")), TestActor);
        ExpectMsg<ArtifactRegistered>();

        _registry.Tell(new RegisterArtifact(MakeArtifact("art-002", type: ArtifactType.Image, hash: "h2", format: "png", uri: "file:img.png")), TestActor);
        ExpectMsg<ArtifactRegistered>();

        _registry.Tell(new RegisterArtifact(MakeArtifact("art-003", type: ArtifactType.Code, hash: "h3")), TestActor);
        ExpectMsg<ArtifactRegistered>();

        _registry.Tell(new GetArtifactsByType(ArtifactType.Code), TestActor);
        var result = ExpectMsg<ArtifactListResult>();
        Assert.Equal(2, result.Artifacts.Count);
        Assert.All(result.Artifacts, a => Assert.Equal(ArtifactType.Code, a.Type));
    }

    [Fact]
    public void DuplicateHash_ReturnsExistingId()
    {
        _registry.Tell(new RegisterArtifact(MakeArtifact("art-001", hash: "same-hash")), TestActor);
        var first = ExpectMsg<ArtifactRegistered>();
        Assert.Equal("art-001", first.ArtifactId);

        _registry.Tell(new RegisterArtifact(MakeArtifact("art-002", hash: "same-hash")), TestActor);
        var second = ExpectMsg<ArtifactRegistered>();
        Assert.Equal("art-001", second.ArtifactId); // dedup returns original
    }

    [Fact]
    public void NullHash_NoDedup()
    {
        _registry.Tell(new RegisterArtifact(MakeArtifact("art-001", hash: null)), TestActor);
        var first = ExpectMsg<ArtifactRegistered>();
        Assert.Equal("art-001", first.ArtifactId);

        _registry.Tell(new RegisterArtifact(MakeArtifact("art-002", hash: null)), TestActor);
        var second = ExpectMsg<ArtifactRegistered>();
        Assert.Equal("art-002", second.ArtifactId); // both registered
    }

    [Fact]
    public void UpdateValidation_AppendsResult()
    {
        _registry.Tell(new RegisterArtifact(MakeArtifact()), TestActor);
        ExpectMsg<ArtifactRegistered>();

        _registry.Tell(new UpdateValidation("art-001", new ValidatorResult("compile", true)), TestActor);
        ExpectMsg<ArtifactValidationUpdated>();

        _registry.Tell(new UpdateValidation("art-001", new ValidatorResult("unit-tests", false, "2 failures")), TestActor);
        ExpectMsg<ArtifactValidationUpdated>();

        _registry.Tell(new GetArtifact("art-001"), TestActor);
        var result = ExpectMsg<ArtifactResult>();
        Assert.NotNull(result.Artifact?.Validators);
        Assert.Equal(2, result.Artifact!.Validators!.Count);
        Assert.True(result.Artifact.Validators[0].Passed);
        Assert.False(result.Artifact.Validators[1].Passed);
        Assert.Equal("2 failures", result.Artifact.Validators[1].Details);
    }

    [Fact]
    public void BlessArtifact_PublishesToEventStream()
    {
        _registry.Tell(new RegisterArtifact(MakeArtifact()), TestActor);
        ExpectMsg<ArtifactRegistered>();

        Sys.EventStream.Subscribe(TestActor, typeof(ArtifactBlessed));

        _registry.Tell(new BlessArtifact("art-001"), TestActor);

        // Direct reply
        var reply = ExpectMsg<ArtifactBlessed>();
        Assert.Equal("art-001", reply.ArtifactId);

        // EventStream publication
        var pub = ExpectMsg<ArtifactBlessed>();
        Assert.Equal("art-001", pub.ArtifactId);
    }

    [Fact]
    public void GetByTask_EmptyTask_ReturnsEmptyList()
    {
        _registry.Tell(new GetArtifactsByTask("no-such-task"), TestActor);
        var result = ExpectMsg<ArtifactListResult>();
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void GetByType_NoArtifacts_ReturnsEmptyList()
    {
        _registry.Tell(new GetArtifactsByType(ArtifactType.Audio), TestActor);
        var result = ExpectMsg<ArtifactListResult>();
        Assert.Empty(result.Artifacts);
    }
}
