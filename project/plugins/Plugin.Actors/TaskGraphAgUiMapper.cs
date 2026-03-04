using GiantIsopod.Contracts.Core;
using GiantIsopod.Contracts.Protocol.AgUi;

namespace GiantIsopod.Plugin.Actors;

internal static class TaskGraphAgUiMapper
{
    public static string GraphAgentId(string graphId) => $"graph:{graphId}";

    public static IReadOnlyList<object> MapGraphSubmitted(NotifyTaskGraphSubmitted submitted)
    {
        return
        [
            new RunStartedEvent(submitted.GraphId, submitted.GraphId),
            new CustomEvent(
                "task_graph_submitted",
                new Dictionary<string, object?>
                {
                    ["graphId"] = submitted.GraphId,
                    ["nodeCount"] = submitted.Nodes.Count,
                    ["edgeCount"] = submitted.Edges.Count
                })
        ];
    }

    public static IReadOnlyList<object> MapTaskStatusChanged(NotifyTaskNodeStatusChanged statusChanged)
    {
        var events = new List<object>
        {
            new CustomEvent(
                "task_status",
                new Dictionary<string, object?>
                {
                    ["graphId"] = statusChanged.GraphId,
                    ["taskId"] = statusChanged.TaskId,
                    ["status"] = statusChanged.Status.ToString(),
                    ["agentId"] = statusChanged.AgentId
                })
        };

        switch (statusChanged.Status)
        {
            case TaskNodeStatus.Planning:
                events.Add(new StepStartedEvent(statusChanged.GraphId, statusChanged.TaskId, "planning"));
                break;
            case TaskNodeStatus.Dispatched:
                events.Add(new StepStartedEvent(statusChanged.GraphId, statusChanged.TaskId, "execution"));
                break;
            case TaskNodeStatus.WaitingForSubtasks:
                events.Add(new StepStartedEvent(statusChanged.GraphId, statusChanged.TaskId, "waiting_for_subtasks"));
                break;
            case TaskNodeStatus.Synthesizing:
                events.Add(new StepStartedEvent(statusChanged.GraphId, statusChanged.TaskId, "synthesis"));
                break;
            case TaskNodeStatus.Validating:
                events.Add(new StepStartedEvent(statusChanged.GraphId, statusChanged.TaskId, "validation"));
                break;
            case TaskNodeStatus.Completed:
                events.Add(new StepFinishedEvent(statusChanged.GraphId, statusChanged.TaskId, "completed"));
                break;
            case TaskNodeStatus.Failed:
                events.Add(new RunErrorEvent(statusChanged.GraphId, statusChanged.TaskId, "Task failed."));
                break;
            case TaskNodeStatus.Cancelled:
                events.Add(new RunErrorEvent(statusChanged.GraphId, statusChanged.TaskId, "Task cancelled."));
                break;
        }

        return events;
    }

    public static IReadOnlyList<object> MapGraphCompleted(TaskGraphCompleted completed)
    {
        return
        [
            new CustomEvent(
                "task_graph_completed",
                new Dictionary<string, object?>
                {
                    ["graphId"] = completed.GraphId,
                    ["succeeded"] = completed.Results.Values.Count(v => v),
                    ["total"] = completed.Results.Count
                }),
            new RunFinishedEvent(completed.GraphId, completed.GraphId)
        ];
    }
}
