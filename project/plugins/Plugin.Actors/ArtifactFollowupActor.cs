using Akka.Actor;
using Akka.Pattern;
using GiantIsopod.Contracts.Core;
using Microsoft.Extensions.Logging;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// Watches blessed artifacts and turns them into concrete follow-up task suggestions.
/// The first cut is intentionally conservative: it suggests test/docs follow-up for code artifacts.
/// </summary>
public sealed class ArtifactFollowupActor : UntypedActor
{
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(5);

    private readonly IActorRef _artifacts;
    private readonly IActorRef _taskGraph;
    private readonly ILogger<ArtifactFollowupActor> _logger;
    private readonly Dictionary<string, IReadOnlyList<ArtifactFollowupSuggestion>> _suggestions = new(StringComparer.Ordinal);

    public ArtifactFollowupActor(
        IActorRef artifacts,
        IActorRef taskGraph,
        ILogger<ArtifactFollowupActor> logger)
    {
        _artifacts = artifacts;
        _taskGraph = taskGraph;
        _logger = logger;
    }

    protected override void PreStart()
    {
        Context.System.EventStream.Subscribe(Self, typeof(ArtifactBlessed));
        base.PreStart();
    }

    protected override void PostStop()
    {
        Context.System.EventStream.Unsubscribe(Self, typeof(ArtifactBlessed));
        base.PostStop();
    }

    protected override void OnReceive(object message)
    {
        switch (message)
        {
            case ArtifactBlessed blessed:
                LoadSuggestions(blessed.ArtifactId, publishToStream: true, replyTo: ActorRefs.Nobody);
                break;

            case GetArtifactFollowups get:
                if (_suggestions.TryGetValue(get.ArtifactId, out var existing))
                {
                    Sender.Tell(new ArtifactFollowupResult(get.ArtifactId, existing));
                    break;
                }

                LoadSuggestions(get.ArtifactId, publishToStream: false, replyTo: Sender);
                break;

            case SubmitArtifactFollowup submit:
                if (_suggestions.TryGetValue(submit.ArtifactId, out var suggestions))
                {
                    SubmitFollowupGraph(submit, suggestions, Sender);
                    break;
                }

                LoadSuggestions(submit.ArtifactId, publishToStream: false, replyTo: Sender, pendingSubmission: submit);
                break;

            case ArtifactLoaded loaded:
                HandleArtifactLoaded(loaded);
                break;
        }
    }

    private void LoadSuggestions(
        string artifactId,
        bool publishToStream,
        IActorRef replyTo,
        SubmitArtifactFollowup? pendingSubmission = null)
    {
        _artifacts
            .Ask<ArtifactResult>(new GetArtifact(artifactId), LookupTimeout)
            .ContinueWith(task =>
            {
                if (task.IsFaulted || task.IsCanceled)
                {
                    return (object)new ArtifactLoaded(
                        artifactId,
                        Artifact: null,
                        PublishToStream: publishToStream,
                        ReplyTo: replyTo,
                        PendingSubmission: pendingSubmission,
                        FailureReason: task.Exception?.GetBaseException().Message ?? "artifact lookup timed out");
                }

                return new ArtifactLoaded(
                    artifactId,
                    task.Result.Artifact,
                    publishToStream,
                    replyTo,
                    pendingSubmission,
                    FailureReason: null);
            })
            .PipeTo(Self);
    }

    private void HandleArtifactLoaded(ArtifactLoaded loaded)
    {
        if (loaded.Artifact is null)
        {
            if (loaded.ReplyTo != ActorRefs.Nobody)
            {
                loaded.ReplyTo.Tell(new Status.Failure(
                    new KeyNotFoundException(
                        loaded.FailureReason ?? $"Artifact not found: {loaded.ArtifactId}")));
            }
            return;
        }

        var suggestions = BuildSuggestions(loaded.Artifact);
        _suggestions[loaded.ArtifactId] = suggestions;

        if (loaded.PublishToStream && suggestions.Count > 0)
            Context.System.EventStream.Publish(new ArtifactFollowupSuggested(loaded.ArtifactId, suggestions));

        if (loaded.PendingSubmission is not null)
        {
            SubmitFollowupGraph(loaded.PendingSubmission, suggestions, loaded.ReplyTo);
            return;
        }

        if (loaded.ReplyTo != ActorRefs.Nobody)
            loaded.ReplyTo.Tell(new ArtifactFollowupResult(loaded.ArtifactId, suggestions));
    }

    private void SubmitFollowupGraph(
        SubmitArtifactFollowup submit,
        IReadOnlyList<ArtifactFollowupSuggestion> suggestions,
        IActorRef replyTo)
    {
        var selected = submit.SuggestionIds is { Count: > 0 }
            ? suggestions.Where(s => submit.SuggestionIds.Contains(s.SuggestionId, StringComparer.Ordinal)).ToArray()
            : suggestions.ToArray();

        if (selected.Length == 0)
        {
            replyTo.Tell(new Status.Failure(
                new InvalidOperationException($"No artifact follow-up suggestions selected for {submit.ArtifactId}.")));
            return;
        }

        var graphId = !string.IsNullOrWhiteSpace(submit.GraphId)
            ? submit.GraphId!
            : $"artifact-followup-{SanitizeId(submit.ArtifactId)}";

        var nodes = selected
            .Select(s => new TaskNode(
                s.SuggestionId,
                s.Description,
                s.RequiredCapabilities,
                RequiredValidators: s.RequiredValidators,
                PreferredRuntimeId: s.PreferredRuntimeId,
                OwnedPaths: s.OwnedPaths,
                ExpectedFiles: s.ExpectedFiles))
            .ToArray();

        _taskGraph.Tell(new SubmitTaskGraph(graphId, nodes, Array.Empty<TaskEdge>()));
        replyTo.Tell(new ArtifactFollowupSubmitted(submit.ArtifactId, graphId, nodes.Select(n => n.TaskId).ToArray()));
        _logger.LogInformation(
            "Submitted artifact follow-up graph {GraphId} for artifact {ArtifactId} with {Count} task(s)",
            graphId,
            submit.ArtifactId,
            nodes.Length);
    }

    private static IReadOnlyList<ArtifactFollowupSuggestion> BuildSuggestions(ArtifactRef artifact)
    {
        var suggestions = new List<ArtifactFollowupSuggestion>();
        var relativePath = GetRelativePath(artifact);

        if (artifact.Type == ArtifactType.Code &&
            !string.IsNullOrWhiteSpace(relativePath) &&
            !relativePath.StartsWith("project/tests/", StringComparison.OrdinalIgnoreCase))
        {
            suggestions.Add(new ArtifactFollowupSuggestion(
                SuggestionId: $"{artifact.ArtifactId}:tests",
                ArtifactId: artifact.ArtifactId,
                Title: "Add or update tests",
                Description: BuildTestFollowupDescription(artifact, relativePath),
                RequiredCapabilities: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "testing" },
                OwnedPaths: new[] { "project/tests" },
                RequiredValidators: new[] { "agent-review" },
                PreferredRuntimeId: "pi"));
        }

        if (artifact.Type == ArtifactType.Code &&
            !string.IsNullOrWhiteSpace(relativePath) &&
            (relativePath.StartsWith("project/contracts/", StringComparison.OrdinalIgnoreCase) ||
             relativePath.StartsWith("project/plugins/", StringComparison.OrdinalIgnoreCase)))
        {
            suggestions.Add(new ArtifactFollowupSuggestion(
                SuggestionId: $"{artifact.ArtifactId}:docs",
                ArtifactId: artifact.ArtifactId,
                Title: "Update docs for behavior changes",
                Description: BuildDocsFollowupDescription(artifact, relativePath),
                RequiredCapabilities: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "documentation" },
                OwnedPaths: new[] { "docs", "DOGFOOD.md" },
                PreferredRuntimeId: "kimi"));
        }

        return suggestions;
    }

    private static string BuildTestFollowupDescription(ArtifactRef artifact, string relativePath)
    {
        return
            $"Artifact follow-up for '{artifact.ArtifactId}'. " +
            $"A code artifact was blessed at '{relativePath}'. " +
            $"Add or update automated tests that cover the behavior introduced or changed by that artifact. " +
            $"Read the source artifact and any relevant nearby tests, but edit only files under project/tests. " +
            $"Do not modify '{relativePath}' itself.";
    }

    private static string BuildDocsFollowupDescription(ArtifactRef artifact, string relativePath)
    {
        return
            $"Artifact follow-up for '{artifact.ArtifactId}'. " +
            $"A code artifact was blessed at '{relativePath}'. " +
            $"Update documentation only if the change affects developer workflow, runtime behavior, or public task/validator/runtime contracts. " +
            $"Edit only documentation files under docs or DOGFOOD.md. " +
            $"Do not modify code files.";
    }

    private static string? GetRelativePath(ArtifactRef artifact)
    {
        if (artifact.Metadata is null ||
            !artifact.Metadata.TryGetValue("relativePath", out var relativePath) ||
            string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        return NormalizePath(relativePath);
    }

    private static string SanitizeId(string value)
    {
        var chars = value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        return new string(chars).Trim('-');
    }

    private static string NormalizePath(string path)
    {
        var normalized = path.Trim().Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];

        return normalized.Trim('/');
    }

    private sealed record ArtifactLoaded(
        string ArtifactId,
        ArtifactRef? Artifact,
        bool PublishToStream,
        IActorRef ReplyTo,
        SubmitArtifactFollowup? PendingSubmission,
        string? FailureReason);
}
