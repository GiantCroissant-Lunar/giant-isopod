using Akka.Actor;
using GiantIsopod.Contracts.Core;
using Microsoft.Extensions.Logging;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// /user/a2a — Agent-to-Agent protocol actor.
/// Handles A2A task submission, task status queries, and agent card discovery.
/// Subscribes to EventStream for TaskCompleted/TaskFailed to track task state.
/// </summary>
public sealed class A2AActor : UntypedActor
{
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

    protected override void PreStart()
    {
        // Subscribe to task completion events
        Context.System.EventStream.Subscribe(Self, typeof(TaskCompleted));
        Context.System.EventStream.Subscribe(Self, typeof(TaskFailed));
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
                    tc.Status = "completed";
                    tc.Summary = completed.Summary;
                    _logger.LogDebug("A2A task {TaskId} completed", completed.TaskId);
                }
                break;

            case TaskFailed failed:
                if (_tasks.TryGetValue(failed.TaskId, out var tf))
                {
                    tf.Status = "failed";
                    tf.Summary = failed.Reason;
                    _logger.LogDebug("A2A task {TaskId} failed: {Reason}", failed.TaskId, failed.Reason);
                }
                break;

            case TaskAssigned assigned:
                if (_tasks.TryGetValue(assigned.TaskId, out var ta))
                {
                    ta.Status = "working";
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
            Status = "submitted"
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
            return $"{{\"taskId\":\"{taskId}\",\"status\":\"unknown\"}}";

        return $"{{\"taskId\":\"{task.TaskId}\",\"status\":\"{task.Status}\"" +
               (task.AssignedAgentId != null ? $",\"agentId\":\"{task.AssignedAgentId}\"" : "") +
               (task.Summary != null ? $",\"summary\":\"{EscapeJson(task.Summary)}\"" : "") +
               "}";
    }

    private static string EscapeJson(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private sealed class A2AInternalTask
    {
        public required string TaskId { get; set; }
        public required string Description { get; set; }
        public string? TargetAgentId { get; set; }
        public string? AssignedAgentId { get; set; }
        public string Status { get; set; } = "submitted";
        public string? Summary { get; set; }
    }

}
