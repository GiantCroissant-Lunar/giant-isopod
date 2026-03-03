using System.Globalization;
using System.Security;
using System.Text;
using GiantIsopod.Contracts.Core;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// Builds structured prompts that inject retrieved knowledge context
/// before task descriptions, using XML-style sections that LLMs can parse.
/// </summary>
public static class PromptBuilder
{
    private const string ResultEnvelopeTag = "giant-isopod-result";

    public static string BuildTaskPrompt(string taskId, string description, IReadOnlyList<KnowledgeEntry>? entries = null)
    {
        var sb = new StringBuilder();

        if (entries is { Count: > 0 })
        {
            sb.AppendLine("Knowledge context:");
            foreach (var entry in entries)
            {
                sb.Append("- [category=").Append(SecurityElement.Escape(entry.Category));
                sb.Append(" relevance=").Append(entry.Relevance.ToString("F2", CultureInfo.InvariantCulture));
                if (entry.Tags is { Count: > 0 })
                {
                    foreach (var (key, value) in entry.Tags)
                        sb.Append(' ').Append(key).Append('=').Append(SecurityElement.Escape(value));
                }
                sb.Append("] ");
                sb.AppendLine(SecurityElement.Escape(entry.Content));
            }
            sb.AppendLine();
        }

        sb.Append("Task ID: ").AppendLine(taskId);
        sb.AppendLine("Task:");
        sb.AppendLine(description);
        sb.AppendLine();
        AppendStructuredResultContract(sb, taskId);
        return sb.ToString();
    }

    /// <summary>
    /// Builds a synthesis prompt from completed subtask results, instructing the agent
    /// to produce a final deliverable for the parent task.
    /// </summary>
    public static string BuildSynthesisPrompt(string parentTaskId, IReadOnlyList<TaskCompleted> results)
    {
        var sb = new StringBuilder();
        sb.Append("Parent task: ").AppendLine(parentTaskId);
        sb.AppendLine("Synthesize the completed subtask results into one final deliverable.");
        sb.AppendLine("Subtask results:");
        foreach (var result in results)
        {
            sb.Append("- task=").Append(SecurityElement.Escape(result.TaskId));
            sb.Append(" success=").Append(result.Success.ToString(CultureInfo.InvariantCulture).ToLowerInvariant());
            sb.Append(" agent=").Append(SecurityElement.Escape(result.AgentId));
            sb.Append(" summary=");
            if (result.Summary != null)
                sb.Append(SecurityElement.Escape(result.Summary));
            sb.AppendLine();
        }
        sb.AppendLine("Produce a concise summary and include any artifacts in the final result envelope.");
        sb.AppendLine();
        AppendStructuredResultContract(sb, parentTaskId);
        return sb.ToString();
    }

    /// <summary>
    /// Builds an enriched prompt with knowledge context preceding the task description.
    /// Entries are formatted as structured XML blocks preserving category and relevance metadata.
    /// </summary>
    public static string BuildEnrichedPrompt(string description, IReadOnlyList<KnowledgeEntry> entries)
    {
        return BuildTaskPrompt("task", description, entries);
    }

    private static void AppendStructuredResultContract(StringBuilder sb, string taskId)
    {
        sb.AppendLine("Result contract:");
        sb.Append("Assigned task id: ").AppendLine(SecurityElement.Escape(taskId));
        sb.AppendLine("- Return the final machine-readable result envelope as the last thing in your response.");
        sb.AppendLine("- Do not wrap the envelope in markdown fences.");
        sb.AppendLine("- task_id must exactly match the assigned task id.");
        sb.AppendLine("- outcome must be completed, failed, or decompose.");
        sb.AppendLine("- Always include a concise summary.");
        sb.AppendLine("- Always include artifacts_expected as an array of artifact types you changed or expect to produce. Use [] when there are none.");
        sb.AppendLine("- If the task should be decomposed, populate subplan instead of guessing.");
        sb.AppendLine("- If you cannot complete the task and decomposition is not appropriate, set failure_reason.");
        sb.AppendLine("Schema examples:");
        sb.AppendLine("{\"task_id\":\"string\",\"outcome\":\"completed\",\"summary\":\"string\",\"artifacts_expected\":[\"Code\"],\"failure_reason\":null,\"subplan\":null}");
        sb.AppendLine("{\"task_id\":\"string\",\"outcome\":\"failed\",\"summary\":\"string\",\"artifacts_expected\":[],\"failure_reason\":\"string\",\"subplan\":null}");
        sb.AppendLine("{\"task_id\":\"string\",\"outcome\":\"decompose\",\"summary\":\"string\",\"artifacts_expected\":[],\"failure_reason\":null,\"subplan\":{\"reason\":\"TooLarge\",\"subtasks\":[{\"description\":\"string\",\"required_capabilities\":[\"coding\"],\"depends_on_subtasks\":[],\"budget_cap_seconds\":300,\"expected_output_types\":[\"Code\"]}],\"stop_when\":{\"kind\":\"AllSubtasksComplete\",\"description\":\"string\"}}}");
        sb.AppendLine("Example:");
        sb.AppendLine($"<{ResultEnvelopeTag}>");
        sb.AppendLine("{\"task_id\":\"string\",\"outcome\":\"completed\",\"summary\":\"string\",\"artifacts_expected\":[\"Code\"],\"failure_reason\":null,\"subplan\":null}");
        sb.AppendLine($"</{ResultEnvelopeTag}>");
        sb.Append("Envelope open tag: <").Append(ResultEnvelopeTag).AppendLine(">");
        sb.Append("Envelope close tag: </").Append(ResultEnvelopeTag).AppendLine(">");
        sb.AppendLine("Allowed reasons: TooLarge, MissingInfo, DependencyDiscovered, Ambiguity, ExternalToolRequired");
        sb.AppendLine("Allowed stop kinds: AllSubtasksComplete, FirstSuccess, UserDecision");
        sb.AppendLine("Allowed artifact types: Code, Doc, Image, Audio, Model3D, App, Dataset, Config");
    }
}
