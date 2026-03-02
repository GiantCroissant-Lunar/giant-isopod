using Akka.Actor;
using GiantIsopod.Contracts.Core;
using Microsoft.Extensions.Logging;

namespace GiantIsopod.Plugin.Actors;

public sealed class ArtifactRegistryActor : UntypedActor
{
    private readonly ILogger<ArtifactRegistryActor> _logger;
    private readonly Dictionary<string, ArtifactRef> _artifacts = new();
    private readonly Dictionary<string, List<string>> _byTask = new();
    private readonly Dictionary<ArtifactType, List<string>> _byType = new();
    private readonly Dictionary<string, string> _byHash = new();
    private readonly HashSet<string> _blessed = new();

    public ArtifactRegistryActor(ILogger<ArtifactRegistryActor> logger)
    {
        _logger = logger;
    }

    protected override void OnReceive(object message)
    {
        switch (message)
        {
            case RegisterArtifact register:
                HandleRegister(register);
                break;

            case GetArtifact get:
                _artifacts.TryGetValue(get.ArtifactId, out var artifact);
                Sender.Tell(new ArtifactResult(artifact));
                break;

            case GetArtifactsByTask getByTask:
                Sender.Tell(new ArtifactListResult(LookupByTask(getByTask.TaskId)));
                break;

            case GetArtifactsByType getByType:
                Sender.Tell(new ArtifactListResult(LookupByType(getByType.Type)));
                break;

            case UpdateValidation update:
                HandleUpdateValidation(update);
                break;

            case BlessArtifact bless:
                HandleBless(bless);
                break;
        }
    }

    private void HandleRegister(RegisterArtifact register)
    {
        var art = register.Artifact;

        if (art.ContentHash is not null && _byHash.TryGetValue(art.ContentHash, out var existingId))
        {
            _logger.LogDebug("Artifact {ArtifactId} is a duplicate of {ExistingId} (hash {Hash})",
                art.ArtifactId, existingId, art.ContentHash);
            Sender.Tell(new ArtifactRegistered(existingId));
            return;
        }

        _artifacts[art.ArtifactId] = art;
        IndexArtifact(art);

        _logger.LogDebug("Registered artifact {ArtifactId} type={Type} format={Format}",
            art.ArtifactId, art.Type, art.Format);

        Sender.Tell(new ArtifactRegistered(art.ArtifactId));
    }

    private void HandleUpdateValidation(UpdateValidation update)
    {
        if (!_artifacts.TryGetValue(update.ArtifactId, out var existing))
        {
            _logger.LogWarning("UpdateValidation for unknown artifact {ArtifactId}", update.ArtifactId);
            Sender.Tell(new Status.Failure(new KeyNotFoundException($"Artifact not found: {update.ArtifactId}")));
            return;
        }

        var validators = existing.Validators?.ToList() ?? new List<ValidatorResult>();
        validators.Add(update.Result);

        _artifacts[update.ArtifactId] = existing with { Validators = validators };

        _logger.LogDebug("Updated validation for {ArtifactId}: {Validator}={Passed}",
            update.ArtifactId, update.Result.ValidatorName, update.Result.Passed);

        Sender.Tell(new ArtifactValidationUpdated(update.ArtifactId));
    }

    private void HandleBless(BlessArtifact bless)
    {
        if (!_artifacts.ContainsKey(bless.ArtifactId))
        {
            _logger.LogWarning("BlessArtifact for unknown artifact {ArtifactId}", bless.ArtifactId);
            Sender.Tell(new Status.Failure(new KeyNotFoundException($"Artifact not found: {bless.ArtifactId}")));
            return;
        }

        var addedToBlessed = _blessed.Add(bless.ArtifactId);

        _logger.LogDebug("Blessed artifact {ArtifactId}", bless.ArtifactId);

        var blessed = new ArtifactBlessed(bless.ArtifactId);
        Sender.Tell(blessed);
        if (addedToBlessed)
        {
            Context.System.EventStream.Publish(blessed);
        }
    }

    private void IndexArtifact(ArtifactRef art)
    {
        if (!_byTask.TryGetValue(art.Provenance.TaskId, out var taskList))
        {
            taskList = new List<string>();
            _byTask[art.Provenance.TaskId] = taskList;
        }
        taskList.Add(art.ArtifactId);

        if (!_byType.TryGetValue(art.Type, out var typeList))
        {
            typeList = new List<string>();
            _byType[art.Type] = typeList;
        }
        typeList.Add(art.ArtifactId);

        if (art.ContentHash is not null)
        {
            _byHash[art.ContentHash] = art.ArtifactId;
        }
    }

    private IReadOnlyList<ArtifactRef> LookupByTask(string taskId)
    {
        if (!_byTask.TryGetValue(taskId, out var ids))
            return Array.Empty<ArtifactRef>();

        return ids
            .Where(id => _artifacts.ContainsKey(id))
            .Select(id => _artifacts[id])
            .ToList();
    }

    private IReadOnlyList<ArtifactRef> LookupByType(ArtifactType type)
    {
        if (!_byType.TryGetValue(type, out var ids))
            return Array.Empty<ArtifactRef>();

        return ids
            .Where(id => _artifacts.ContainsKey(id))
            .Select(id => _artifacts[id])
            .ToList();
    }
}
