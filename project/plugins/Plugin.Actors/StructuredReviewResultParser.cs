using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GiantIsopod.Plugin.Actors;

internal static partial class StructuredReviewResultParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static ParsedReviewResult Parse(string output, string validatorName, string artifactId)
    {
        foreach (Match match in ReviewEnvelopeRegex().Matches(output))
        {
            var json = match.Groups["json"].Value.Trim();
            if (string.IsNullOrWhiteSpace(json))
                continue;

            try
            {
                var dto = JsonSerializer.Deserialize<ReviewEnvelopeDto>(json, JsonOptions);
                if (dto is null)
                    continue;

                return new ParsedReviewResult(
                    HasEnvelope: true,
                    ValidatorName: string.IsNullOrWhiteSpace(dto.Validator) ? null : dto.Validator.Trim(),
                    ArtifactId: string.IsNullOrWhiteSpace(dto.ArtifactId) ? null : dto.ArtifactId.Trim(),
                    Passed: dto.Passed,
                    Summary: string.IsNullOrWhiteSpace(dto.Summary) ? null : dto.Summary.Trim(),
                    Issues: dto.Issues?.Where(issue => !string.IsNullOrWhiteSpace(issue)).Select(issue => issue.Trim()).ToArray()
                        ?? Array.Empty<string>());
            }
            catch (JsonException)
            {
            }
        }

        return new ParsedReviewResult(
            HasEnvelope: false,
            ValidatorName: validatorName,
            ArtifactId: artifactId,
            Passed: false,
            Summary: null,
            Issues: Array.Empty<string>());
    }

    [GeneratedRegex(@"<giant-isopod-review>\s*(?<json>\{.*?\})\s*</giant-isopod-review>", RegexOptions.Singleline | RegexOptions.RightToLeft)]
    private static partial Regex ReviewEnvelopeRegex();

    internal sealed record ParsedReviewResult(
        bool HasEnvelope,
        string? ValidatorName,
        string? ArtifactId,
        bool Passed,
        string? Summary,
        IReadOnlyList<string> Issues);

    private sealed record ReviewEnvelopeDto(
        [property: JsonPropertyName("validator")] string? Validator,
        [property: JsonPropertyName("artifact_id")] string? ArtifactId,
        [property: JsonPropertyName("passed")] bool Passed,
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("issues")] IReadOnlyList<string>? Issues);
}
