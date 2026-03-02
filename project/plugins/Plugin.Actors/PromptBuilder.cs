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
    /// <summary>
    /// Builds an enriched prompt with knowledge context preceding the task description.
    /// Entries are formatted as structured XML blocks preserving category and relevance metadata.
    /// </summary>
    public static string BuildEnrichedPrompt(string description, IReadOnlyList<KnowledgeEntry> entries)
    {
        if (entries.Count == 0)
            return description;

        var sb = new StringBuilder();
        sb.AppendLine("<knowledge-context>");
        foreach (var entry in entries)
        {
            sb.Append("  <entry category=\"").Append(SecurityElement.Escape(entry.Category)).Append('"');
            sb.Append(" relevance=\"").Append(entry.Relevance.ToString("F2", CultureInfo.InvariantCulture)).Append('"');
            if (entry.Tags is { Count: > 0 })
            {
                foreach (var (key, value) in entry.Tags)
                    sb.Append(' ').Append(key).Append("=\"").Append(SecurityElement.Escape(value)).Append('"');
            }
            sb.Append('>');
            sb.Append(SecurityElement.Escape(entry.Content));
            sb.AppendLine("</entry>");
        }
        sb.AppendLine("</knowledge-context>");
        sb.AppendLine();
        sb.AppendLine("<task>");
        sb.Append(description);
        sb.AppendLine();
        sb.Append("</task>");

        return sb.ToString();
    }
}
