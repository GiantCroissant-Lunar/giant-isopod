using Akka.Actor;
using GiantIsopod.Contracts.Core;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// /user/agents/{name}/tasks — tracks active task lifecycle for an agent.
/// </summary>
public sealed class AgentTaskActor : UntypedActor
{
    private readonly string _agentId;
    private readonly Dictionary<string, TaskState> _activeTasks = new();

    public AgentTaskActor(string agentId)
    {
        _agentId = agentId;
    }

    protected override void OnReceive(object message)
    {
        switch (message)
        {
            case TaskAssigned task:
                _activeTasks[task.TaskId] = new TaskState(task.TaskId, DateTimeOffset.UtcNow);
                break;

            case TaskCompleted completed:
                _activeTasks.Remove(completed.TaskId);
                Context.Parent.Tell(completed);
                break;

            case TaskFailed failed:
                _activeTasks.Remove(failed.TaskId ?? "");
                Context.Parent.Tell(failed);
                break;
        }
    }

    private record TaskState(string TaskId, DateTimeOffset StartedAt);
}
