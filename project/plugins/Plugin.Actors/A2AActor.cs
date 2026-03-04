using Akka.Actor;
using GiantIsopod.Contracts.Core;
using GiantIsopod.Contracts.Protocol.A2A;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// /user/a2a — Agent-to-Agent protocol actor.
/// Handles A2A task submission, task status queries, and agent card discovery.
/// Subscribes to EventStream for TaskCompleted/TaskFailed to track task state.
/// </summary>
public sealed class A2AActor : UntypedActor
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly IActorRef _dispatch;
    private readonly IActorRef _registry;
    private readonly ILogger<A2AActor> _logger;

    // Internal task state: taskId → (status, description, agentId?)
    private readonly Dictionary<string, A2AInternalTask> _tasks = new();

    public A2AActor(IActorRef dispatch, IActorRef registry, ILogger<A2AActor> logger)
    {
        _dispatch = dispatch;
        _registry = registry;
        _logger = logger;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    protected override void PreStart()
    {
        // Subscribe to task completion events
        Context.System.EventStream.Subscribe(Self, typeof(TaskCompleted));
        Context.System.EventStream.Subscribe(Self, typeof(TaskFailed));
        Context.System.EventStream.Subscribe(Self, typeof(TaskAssigned));
        _logger.LogInformation("A2AActor started");
    }

    protected override void OnReceive(object message)
    {
        switch (message)
        {
            case A2ASendTask sendTask:
                HandleSendTask(sendTask);
                break;

            case A2AGetTask getTask:
                HandleGetTask(getTask);
                break;

            case QueryAgentCards queryCards:
                HandleQueryAgentCards(queryCards);
                break;

            case TaskCompleted completed:
                if (_tasks.TryGetValue(completed.TaskId, out var tc))
                {
                    tc.Status = A2ATaskStatus.Completed;
                    tc.CompletionSummary = completed.Summary;
                    tc.Artifacts = MapArtifacts(completed.Artifacts);
                    _logger.LogDebug("A2A task {TaskId} completed", completed.TaskId);
                }
                break;

            case TaskFailed failed:
                if (_tasks.TryGetValue(failed.TaskId, out var tf))
                {
                    tf.Status = A2ATaskStatus.Failed;
                    tf.CompletionSummary = failed.Reason;
                    _logger.LogDebug("A2A task {TaskId} failed: {Reason}", failed.TaskId, failed.Reason);
                }
                break;

            case TaskAssigned assigned:
                if (_tasks.TryGetValue(assigned.TaskId, out var ta))
                {
                    ta.Status = A2ATaskStatus.Working;
                    ta.AssignedAgentId = assigned.AgentId;
                }
                break;
        }
    }

    private void HandleSendTask(A2ASendTask sendTask)
    {
        _tasks[sendTask.TaskId] = new A2AInternalTask
        {
            TaskId = sendTask.TaskId,
            Description = sendTask.Description,
            TargetAgentId = sendTask.TargetAgentId,
            Status = A2ATaskStatus.Submitted,
            History =
            [
                new A2AMessage(
                    "user",
                    BuildInitialParts(sendTask.Description, sendTask.PayloadJson))
            ]
        };

        _logger.LogInformation("A2A task submitted: {TaskId} → {TargetAgentId}", sendTask.TaskId, sendTask.TargetAgentId);

        // Forward as a TaskRequest to dispatch
        var capabilities = new HashSet<string>();
        _dispatch.Tell(new TaskRequest(sendTask.TaskId, sendTask.Description, capabilities));

        Sender.Tell(new A2ATaskResult(sendTask.TaskId, BuildStatusJson(sendTask.TaskId)));
    }

    private void HandleGetTask(A2AGetTask getTask)
    {
        var json = BuildStatusJson(getTask.TaskId);
        Sender.Tell(new A2ATaskResult(getTask.TaskId, json));
    }

    private void HandleQueryAgentCards(QueryAgentCards query)
    {
        // Query registry for capable agents and build cards
        var caps = query.RequiredCapabilities ?? (IReadOnlySet<string>)new HashSet<string>();
        var sender = Sender;

        _registry.Ask<CapableAgentsResult>(new QueryCapableAgents(caps), TimeSpan.FromSeconds(5))
            .ContinueWith(task =>
            {
                if (task.IsCompletedSuccessfully)
                {
                    var cards = task.Result.AgentIds
                        .Select(id => new AgentCardInfo(id, id))
                        .ToList();
                    return new AgentCardsResult(cards);
                }
                return new AgentCardsResult(Array.Empty<AgentCardInfo>());
            })
            .PipeTo(sender);
    }

    private string BuildStatusJson(string taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
        {
            var unknown = new A2ATask(
                taskId,
                A2ATaskStatus.Failed,
                History:
                [
                    new A2AMessage("system", [new TextPart("Unknown task id.")])
                ]);
            return JsonSerializer.Serialize(unknown, JsonOptions);
        }

        var history = new List<A2AMessage>(task.History);
        if (!string.IsNullOrWhiteSpace(task.AssignedAgentId) || !string.IsNullOrWhiteSpace(task.CompletionSummary))
        {
            var parts = new List<A2APart>();
            if (!string.IsNullOrWhiteSpace(task.AssignedAgentId))
                parts.Add(new DataPart("application/json", new Dictionary<string, object?> { ["assignedAgentId"] = task.AssignedAgentId }));
            if (!string.IsNullOrWhiteSpace(task.CompletionSummary))
                parts.Add(new TextPart(task.CompletionSummary!));
            history.Add(new A2AMessage("assistant", parts));
        }

        var a2aTask = new A2ATask(
            task.TaskId,
            task.Status,
            History: history,
            Artifacts: task.Artifacts);
        return JsonSerializer.Serialize(a2aTask, JsonOptions);
    }

    private static IReadOnlyList<A2APart> BuildInitialParts(string description, string? payloadJson)
    {
        var parts = new List<A2APart> { new TextPart(description) };
        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            parts.Add(new DataPart(
                "application/json",
                new Dictionary<string, object?> { ["payloadJson"] = payloadJson }));
        }
        return parts;
    }

    private static IReadOnlyList<A2AArtifact>? MapArtifacts(IReadOnlyList<ArtifactRef>? artifacts)
    {
        if (artifacts is not { Count: > 0 })
            return null;

        return artifacts.Select(artifact =>
        {
            var parts = new List<A2APart>
            {
                new DataPart("application/json", new Dictionary<string, object?>
                {
                    ["type"] = artifact.Type.ToString(),
                    ["format"] = artifact.Format,
                    ["uri"] = artifact.Uri,
                    ["contentHash"] = artifact.ContentHash
                })
            };

            return new A2AArtifact(
                artifact.ArtifactId,
                artifact.Metadata != null && artifact.Metadata.TryGetValue("relativePath", out var relativePath)
                    ? relativePath
                    : artifact.ArtifactId,
                parts,
                artifact.Type.ToString());
        }).ToArray();
    }

    private sealed class A2AInternalTask
    {
        public required string TaskId { get; set; }
        public required string Description { get; set; }
        public string? TargetAgentId { get; set; }
        public string? AssignedAgentId { get; set; }
        public A2ATaskStatus Status { get; set; } = A2ATaskStatus.Submitted;
        public List<A2AMessage> History { get; set; } = new();
        public string? CompletionSummary { get; set; }
        public IReadOnlyList<A2AArtifact>? Artifacts { get; set; }
    }

}
