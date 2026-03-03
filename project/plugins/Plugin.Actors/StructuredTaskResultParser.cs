using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GiantIsopod.Contracts.Core;

namespace GiantIsopod.Plugin.Actors;

public static partial class StructuredTaskResultParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static ParsedTaskResult Parse(string output, string taskId)
    {
        foreach (Match match in ResultEnvelopeRegex().Matches(output))
        {
            var json = match.Groups["json"].Value.Trim();
            if (string.IsNullOrWhiteSpace(json))
                continue;

            try
            {
                var dto = JsonSerializer.Deserialize<ResultEnvelopeDto>(json, JsonOptions);
                if (dto is null)
                    continue;

                var subplan = dto.Subplan is null
                    ? null
                    : new ProposedSubplan(
                        taskId,
                        dto.Subplan.Reason,
                        dto.Subplan.Subtasks.Select(MapSubtask).ToArray(),
                        dto.Subplan.StopWhen is null
                            ? null
                            : new StopCondition(dto.Subplan.StopWhen.Kind, dto.Subplan.StopWhen.Description));

                var outcome = ParseOutcome(dto.Outcome) ?? InferOutcome(dto);
                var expectedArtifactTypes = dto.ArtifactsExpected ?? Array.Empty<ArtifactType>();

                return new ParsedTaskResult(
                    HasEnvelope: true,
                    EnvelopeTaskId: string.IsNullOrWhiteSpace(dto.TaskId) ? null : dto.TaskId.Trim(),
                    Outcome: outcome,
                    Summary: string.IsNullOrWhiteSpace(dto.Summary) ? null : dto.Summary.Trim(),
                    FailureReason: string.IsNullOrWhiteSpace(dto.FailureReason) ? null : dto.FailureReason.Trim(),
                    NoOp: dto.NoOp ?? false,
                    Subplan: subplan,
                    ExpectedArtifactTypes: expectedArtifactTypes);
            }
            catch (JsonException)
            {
            }
        }

        return new ParsedTaskResult(
            HasEnvelope: false,
            EnvelopeTaskId: null,
            Outcome: ParsedTaskOutcome.Unknown,
            Summary: null,
            FailureReason: null,
            NoOp: false,
            Subplan: null,
            ExpectedArtifactTypes: Array.Empty<ArtifactType>());
    }

    private static ParsedTaskOutcome InferOutcome(ResultEnvelopeDto dto)
    {
        if (!string.IsNullOrWhiteSpace(dto.FailureReason))
            return ParsedTaskOutcome.Failed;

        if (dto.Subplan is not null)
            return ParsedTaskOutcome.Decompose;

        return ParsedTaskOutcome.Completed;
    }

    private static ParsedTaskOutcome? ParseOutcome(string? outcome)
    {
        if (string.IsNullOrWhiteSpace(outcome))
            return null;

        return outcome.Trim().ToLowerInvariant() switch
        {
            "completed" => ParsedTaskOutcome.Completed,
            "failed" => ParsedTaskOutcome.Failed,
            "decompose" => ParsedTaskOutcome.Decompose,
            _ => null
        };
    }

    private static SubtaskProposal MapSubtask(SubtaskDto dto)
    {
        var budget = dto.BudgetCapSeconds is null
            ? (TimeSpan?)null
            : TimeSpan.FromSeconds(dto.BudgetCapSeconds.Value);

        return new SubtaskProposal(
            dto.Description,
            dto.RequiredCapabilities is null ? new HashSet<string>() : new HashSet<string>(dto.RequiredCapabilities),
            dto.DependsOnSubtasks ?? Array.Empty<string>(),
            budget,
            dto.ExpectedOutputTypes,
            dto.OwnedPaths,
            dto.ExpectedFiles,
            dto.AllowNoOpCompletion ?? false);
    }

    [GeneratedRegex(@"<giant-isopod-result>\s*(?<json>\{.*?\})\s*</giant-isopod-result>", RegexOptions.Singleline | RegexOptions.RightToLeft)]
    private static partial Regex ResultEnvelopeRegex();

    public sealed record ParsedTaskResult(
        bool HasEnvelope,
        string? EnvelopeTaskId,
        ParsedTaskOutcome Outcome,
        string? Summary,
        string? FailureReason,
        bool NoOp,
        ProposedSubplan? Subplan,
        IReadOnlyList<ArtifactType> ExpectedArtifactTypes);

    public enum ParsedTaskOutcome
    {
        Unknown = 0,
        Completed,
        Failed,
        Decompose
    }

    private sealed record ResultEnvelopeDto(
        [property: JsonPropertyName("task_id")] string? TaskId,
        string? Outcome,
        string? Summary,
        [property: JsonPropertyName("no_op")] bool? NoOp,
        [property: JsonPropertyName("failure_reason")] string? FailureReason,
        [property: JsonPropertyName("artifacts_expected")] IReadOnlyList<ArtifactType>? ArtifactsExpected,
        SubplanDto? Subplan);

    private sealed record SubplanDto(
        DecompositionReason Reason,
        IReadOnlyList<SubtaskDto> Subtasks,
        [property: JsonPropertyName("stop_when")] StopWhenDto? StopWhen);

    private sealed record StopWhenDto(
        StopKind Kind,
        string Description);

    private sealed record SubtaskDto(
        string Description,
        [property: JsonPropertyName("required_capabilities")] List<string>? RequiredCapabilities,
        [property: JsonPropertyName("depends_on_subtasks")] IReadOnlyList<string>? DependsOnSubtasks,
        [property: JsonPropertyName("budget_cap_seconds")] int? BudgetCapSeconds,
        [property: JsonPropertyName("expected_output_types")] IReadOnlyList<ArtifactType>? ExpectedOutputTypes,
        [property: JsonPropertyName("owned_paths")] IReadOnlyList<string>? OwnedPaths,
        [property: JsonPropertyName("expected_files")] IReadOnlyList<string>? ExpectedFiles,
        [property: JsonPropertyName("allow_no_op_completion")] bool? AllowNoOpCompletion);
}
