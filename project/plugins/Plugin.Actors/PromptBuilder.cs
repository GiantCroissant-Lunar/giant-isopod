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

    public static string BuildTaskPrompt(
        string taskId,
        string description,
        IReadOnlyList<KnowledgeEntry>? entries = null,
        IReadOnlyList<string>? ownedPaths = null,
        IReadOnlyList<string>? expectedFiles = null,
        bool allowNoOpCompletion = false)
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
        if (ownedPaths is { Count: > 0 })
        {
            sb.AppendLine("Owned paths:");
            foreach (var path in ownedPaths)
                sb.Append("- ").AppendLine(path);
        }

        if (expectedFiles is { Count: > 0 })
        {
            sb.AppendLine("Expected files:");
            foreach (var file in expectedFiles)
                sb.Append("- ").AppendLine(file);
        }

        sb.Append("Allow no-op completion: ").AppendLine(allowNoOpCompletion ? "yes" : "no");

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
        sb.AppendLine("Synthesize the completed subtask results into one final summary.");
        sb.AppendLine("Do not modify files during synthesis unless the parent task explicitly requires a new final artifact.");
        sb.AppendLine("If the subtasks already produced the final deliverables, return a completed result with artifacts_expected=[] and summarize what is already in place.");
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
        sb.AppendLine("Produce a concise summary only. Do not invent new artifacts.");
        sb.AppendLine();
        AppendStructuredResultContract(sb, parentTaskId);
        return sb.ToString();
    }

    public static string BuildDecompositionPrompt(
        string taskId,
        string description,
        IReadOnlySet<string>? executableSkills = null,
        IReadOnlyList<string>? ownedPaths = null,
        IReadOnlyList<string>? expectedFiles = null)
    {
        var sb = new StringBuilder();
        sb.Append("Planner task for parent task ").Append(taskId).AppendLine(".");
        sb.AppendLine("You are responsible only for decomposition.");
        sb.AppendLine("Do not edit files or implement the task.");
        sb.AppendLine("Return outcome=decompose with a concrete subplan, or outcome=completed with no_op=true when the task should execute directly without decomposition.");
        sb.AppendLine("Every subtask must declare owned_paths, expected_files, and allow_no_op_completion.");
        sb.AppendLine("depends_on_subtasks must contain only zero-based numeric subtask indices encoded as strings, such as [] or [\"0\", \"2\"].");
        sb.AppendLine("Do not use symbolic dependency names like subtask-contracts or contracts.");
        sb.AppendLine("When a subtask updates, documents, amends, or records information in an existing file, set allow_no_op_completion=true so reruns can succeed if the file is already in the desired state.");
        if (executableSkills is { Count: > 0 })
        {
            sb.AppendLine("Use only these existing executor skill ids in subtask required_capabilities:");
            foreach (var skill in executableSkills)
                sb.Append("- ").AppendLine(skill);
            sb.AppendLine("Do not invent new capability names like coding, implementation, or editor.");
        }

        if (ownedPaths is { Count: > 0 })
        {
            sb.AppendLine("Owned paths:");
            foreach (var path in ownedPaths)
                sb.Append("- ").AppendLine(path);
        }

        if (expectedFiles is { Count: > 0 })
        {
            sb.AppendLine("Expected files:");
            foreach (var file in expectedFiles)
                sb.Append("- ").AppendLine(file);
        }

        sb.AppendLine("Task:");
        sb.AppendLine(description);
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
        sb.AppendLine("- Stay within owned paths and expected files when they are provided.");
        sb.AppendLine("- If the task is already satisfied with no file changes and no-op completion is allowed, return no_op=true, artifacts_expected=[], and explain why in summary.");
        sb.AppendLine("- If no-op completion is not allowed, do not claim success without the required workspace changes.");
        sb.AppendLine("- Always include artifacts_expected as an array of artifact types you changed or expect to produce. Use [] when there are none.");
        sb.AppendLine("- If the task should be decomposed, populate subplan instead of guessing.");
        sb.AppendLine("- Every subtask in a subplan must declare owned_paths, expected_files, and allow_no_op_completion.");
        sb.AppendLine("- In subplans, depends_on_subtasks must use zero-based numeric indices encoded as strings, not names.");
        sb.AppendLine("- If you cannot complete the task and decomposition is not appropriate, set failure_reason.");
        sb.AppendLine("Schema examples:");
        sb.AppendLine("{\"task_id\":\"string\",\"outcome\":\"completed\",\"summary\":\"string\",\"no_op\":false,\"artifacts_expected\":[\"Code\"],\"failure_reason\":null,\"subplan\":null}");
        sb.AppendLine("{\"task_id\":\"string\",\"outcome\":\"completed\",\"summary\":\"string\",\"no_op\":true,\"artifacts_expected\":[],\"failure_reason\":null,\"subplan\":null}");
        sb.AppendLine("{\"task_id\":\"string\",\"outcome\":\"failed\",\"summary\":\"string\",\"no_op\":false,\"artifacts_expected\":[],\"failure_reason\":\"string\",\"subplan\":null}");
        sb.AppendLine("{\"task_id\":\"string\",\"outcome\":\"decompose\",\"summary\":\"string\",\"no_op\":false,\"artifacts_expected\":[],\"failure_reason\":null,\"subplan\":{\"reason\":\"TooLarge\",\"subtasks\":[{\"description\":\"string\",\"required_capabilities\":[\"code_edit\"],\"depends_on_subtasks\":[],\"budget_cap_seconds\":300,\"expected_output_types\":[\"Code\"],\"owned_paths\":[\"project/path/file.cs\"],\"expected_files\":[\"project/path/file.cs\"],\"allow_no_op_completion\":false},{\"description\":\"string\",\"required_capabilities\":[\"code_edit\"],\"depends_on_subtasks\":[\"0\"],\"budget_cap_seconds\":300,\"expected_output_types\":[\"Code\"],\"owned_paths\":[\"project/path/other.cs\"],\"expected_files\":[\"project/path/other.cs\"],\"allow_no_op_completion\":true}],\"stop_when\":{\"kind\":\"AllSubtasksComplete\",\"description\":\"string\"}}}");
        sb.AppendLine("Example:");
        sb.AppendLine($"<{ResultEnvelopeTag}>");
        sb.AppendLine("{\"task_id\":\"string\",\"outcome\":\"completed\",\"summary\":\"string\",\"no_op\":false,\"artifacts_expected\":[\"Code\"],\"failure_reason\":null,\"subplan\":null}");
        sb.AppendLine($"</{ResultEnvelopeTag}>");
        sb.Append("Envelope open tag: <").Append(ResultEnvelopeTag).AppendLine(">");
        sb.Append("Envelope close tag: </").Append(ResultEnvelopeTag).AppendLine(">");
        sb.AppendLine("Allowed reasons: TooLarge, MissingInfo, DependencyDiscovered, Ambiguity, ExternalToolRequired");
        sb.AppendLine("Allowed stop kinds: AllSubtasksComplete, FirstSuccess, UserDecision");
        sb.AppendLine("Allowed artifact types: Code, Doc, Image, Audio, Model3D, App, Dataset, Config");
    }
}
