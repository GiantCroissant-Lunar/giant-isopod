using GiantIsopod.Contracts.Core;
using GiantIsopod.Plugin.Actors;
using Xunit;

namespace GiantIsopod.Plugin.Actors.Tests;

public class StructuredTaskResultParserTests
{
    [Fact]
    public void Parse_ExtractsSummaryOnlyEnvelope()
    {
        var output = """
thinking
Done.
<giant-isopod-result>
{"task_id":"task-1","outcome":"completed","summary":"Implemented the requested change.","artifacts_expected":["Code"],"failure_reason":null,"subplan":null}
</giant-isopod-result>
""";

        var parsed = StructuredTaskResultParser.Parse(output, "task-1");

        Assert.True(parsed.HasEnvelope);
        Assert.Equal("task-1", parsed.EnvelopeTaskId);
        Assert.Equal(StructuredTaskResultParser.ParsedTaskOutcome.Completed, parsed.Outcome);
        Assert.Equal("Implemented the requested change.", parsed.Summary);
        Assert.Null(parsed.FailureReason);
        Assert.Null(parsed.Subplan);
        Assert.Equal(ArtifactType.Code, Assert.Single(parsed.ExpectedArtifactTypes));
    }

    [Fact]
    public void Parse_ExtractsFailureReason()
    {
        var output = """
<giant-isopod-result>
{"task_id":"task-2","outcome":"failed","summary":"Could not complete safely.","artifacts_expected":[],"failure_reason":"Missing required credentials.","subplan":null}
</giant-isopod-result>
""";

        var parsed = StructuredTaskResultParser.Parse(output, "task-2");

        Assert.True(parsed.HasEnvelope);
        Assert.Equal(StructuredTaskResultParser.ParsedTaskOutcome.Failed, parsed.Outcome);
        Assert.Equal("Could not complete safely.", parsed.Summary);
        Assert.Equal("Missing required credentials.", parsed.FailureReason);
        Assert.Null(parsed.Subplan);
    }

    [Fact]
    public void Parse_ExtractsStructuredSubplan()
    {
        var output = """
<giant-isopod-result>
{
  "task_id": "task-3",
  "outcome": "decompose",
  "summary":"The task should be decomposed.",
  "artifacts_expected": [],
  "failure_reason": null,
  "subplan": {
    "reason": "TooLarge",
    "subtasks": [
      {
        "description": "Inspect the current implementation",
        "required_capabilities": ["analysis"],
        "depends_on_subtasks": [],
        "budget_cap_seconds": 120,
        "expected_output_types": ["Doc"]
      },
      {
        "description": "Implement the actual fix",
        "required_capabilities": ["coding"],
        "depends_on_subtasks": ["0"],
        "budget_cap_seconds": 300,
        "expected_output_types": ["Code"]
      }
    ],
    "stop_when": {
      "kind": "AllSubtasksComplete",
      "description": "All subtasks must finish."
    }
  }
}
</giant-isopod-result>
""";

        var parsed = StructuredTaskResultParser.Parse(output, "task-3");

        Assert.True(parsed.HasEnvelope);
        Assert.Equal("task-3", parsed.EnvelopeTaskId);
        Assert.Equal(StructuredTaskResultParser.ParsedTaskOutcome.Decompose, parsed.Outcome);
        Assert.Equal("The task should be decomposed.", parsed.Summary);
        Assert.NotNull(parsed.Subplan);
        Assert.Equal("task-3", parsed.Subplan!.ParentTaskId);
        Assert.Equal(DecompositionReason.TooLarge, parsed.Subplan.Reason);
        Assert.Equal(2, parsed.Subplan.Subtasks.Count);
        Assert.Equal(TimeSpan.FromSeconds(120), parsed.Subplan.Subtasks[0].BudgetCap);
        Assert.Equal(ArtifactType.Doc, Assert.Single(parsed.Subplan.Subtasks[0].ExpectedOutputTypes!));
        Assert.Equal("0", Assert.Single(parsed.Subplan.Subtasks[1].DependsOnSubtasks));
        Assert.Equal(StopKind.AllSubtasksComplete, parsed.Subplan.StopWhen!.Kind);
    }

    [Fact]
    public void Parse_LegacyEnvelope_RemainsSupported()
    {
        var output = """
<giant-isopod-result>
{"summary":"Legacy response.","failure_reason":null,"subplan":null}
</giant-isopod-result>
""";

        var parsed = StructuredTaskResultParser.Parse(output, "task-legacy");

        Assert.True(parsed.HasEnvelope);
        Assert.Null(parsed.EnvelopeTaskId);
        Assert.Equal(StructuredTaskResultParser.ParsedTaskOutcome.Completed, parsed.Outcome);
        Assert.Equal("Legacy response.", parsed.Summary);
    }

    [Fact]
    public void BuildTaskPrompt_IncludesStructuredContract()
    {
        var prompt = PromptBuilder.BuildTaskPrompt("task-4", "Do the thing.");

        Assert.Contains("Result contract:", prompt, StringComparison.Ordinal);
        Assert.Contains("<giant-isopod-result>", prompt, StringComparison.Ordinal);
        Assert.Contains("Task ID: task-4", prompt, StringComparison.Ordinal);
        Assert.Contains("\"task_id\":\"string\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"outcome\":\"completed\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"artifacts_expected\":[\"Code\"]", prompt, StringComparison.Ordinal);
        Assert.Contains("\"failure_reason\":null", prompt, StringComparison.Ordinal);
        Assert.Contains("Do the thing.", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_UsesLastEnvelopeWhenMultipleEnvelopesPresent()
    {
        var output = """
Let me work on this step by step.

<giant-isopod-result>
{"task_id":"task-multi","outcome":"completed","summary":"First attempt at the change.","artifacts_expected":["Doc"],"failure_reason":null,"subplan":null}
</giant-isopod-result>

Wait, I need to reconsider the approach.

<giant-isopod-result>
{"task_id":"task-multi","outcome":"completed","summary":"Final implementation after revision.","artifacts_expected":["Code"],"failure_reason":null,"subplan":null}
</giant-isopod-result>
""";

        var parsed = StructuredTaskResultParser.Parse(output, "task-multi");

        Assert.True(parsed.HasEnvelope);
        Assert.Equal("task-multi", parsed.EnvelopeTaskId);
        Assert.Equal(StructuredTaskResultParser.ParsedTaskOutcome.Completed, parsed.Outcome);
        Assert.Equal("Final implementation after revision.", parsed.Summary);
        Assert.Null(parsed.FailureReason);
        Assert.Null(parsed.Subplan);
        Assert.Equal(ArtifactType.Code, Assert.Single(parsed.ExpectedArtifactTypes));
    }

    [Fact]
    public void Parse_UsesLastEnvelopeWithDifferentTaskIds()
    {
        var output = """
Analyzing the requirements...

<giant-isopod-result>
{"task_id":"task-prev","outcome":"failed","summary":"Initial approach failed.","artifacts_expected":[],"failure_reason":"Dependency missing.","subplan":null}
</giant-isopod-result>

After fixing the issue:

<giant-isopod-result>
{"task_id":"task-final","outcome":"completed","summary":"Successfully implemented the feature.","artifacts_expected":["Code"],"failure_reason":null,"subplan":null}
</giant-isopod-result>
""";

        var parsed = StructuredTaskResultParser.Parse(output, "task-final");

        Assert.True(parsed.HasEnvelope);
        Assert.Equal("task-final", parsed.EnvelopeTaskId);
        Assert.Equal(StructuredTaskResultParser.ParsedTaskOutcome.Completed, parsed.Outcome);
        Assert.Equal("Successfully implemented the feature.", parsed.Summary);
        Assert.Null(parsed.FailureReason);
        Assert.Null(parsed.Subplan);
        Assert.Equal(ArtifactType.Code, Assert.Single(parsed.ExpectedArtifactTypes));
    }

    [Fact]
    public void Parse_IgnoresEarlierEnvelopeForDifferentTaskId()
    {
        var output = """
<giant-isopod-result>
{"task_id":"task-different","outcome":"completed","summary":"This should be ignored.","artifacts_expected":["Doc"],"failure_reason":null,"subplan":null}
</giant-isopod-result>

Continuing with the actual task...

<giant-isopod-result>
{"task_id":"task-target","outcome":"completed","summary":"This is the correct result.","artifacts_expected":["Code"],"failure_reason":null,"subplan":null}
</giant-isopod-result>
""";

        var parsed = StructuredTaskResultParser.Parse(output, "task-target");

        Assert.True(parsed.HasEnvelope);
        Assert.Equal("task-target", parsed.EnvelopeTaskId);
        Assert.Equal(StructuredTaskResultParser.ParsedTaskOutcome.Completed, parsed.Outcome);
        Assert.Equal("This is the correct result.", parsed.Summary);
        Assert.Null(parsed.FailureReason);
        Assert.Null(parsed.Subplan);
        Assert.Equal(ArtifactType.Code, Assert.Single(parsed.ExpectedArtifactTypes));
    }

    [Fact]
    public void Parse_SkipsEnvelopeWithMismatchingTaskId()
    {
        var output = """
<giant-isopod-result>
{"task_id":"task-previous","outcome":"failed","summary":"Earlier task result.","artifacts_expected":[],"failure_reason":"Some error.","subplan":null}
</giant-isopod-result>

Processing the actual request...

<giant-isopod-result>
{"task_id":"task-actual","outcome":"completed","summary":"Correct task completed.","artifacts_expected":["Code"],"failure_reason":null,"subplan":null}
</giant-isopod-result>
""";

        var parsed = StructuredTaskResultParser.Parse(output, "task-actual");

        Assert.True(parsed.HasEnvelope);
        Assert.Equal("task-actual", parsed.EnvelopeTaskId);
        Assert.Equal(StructuredTaskResultParser.ParsedTaskOutcome.Completed, parsed.Outcome);
        Assert.Equal("Correct task completed.", parsed.Summary);
        Assert.Null(parsed.FailureReason);
        Assert.Null(parsed.Subplan);
        Assert.Equal(ArtifactType.Code, Assert.Single(parsed.ExpectedArtifactTypes));
    }

    [Fact]
    public void Parse_IgnoresEarlierEnvelopeForDifferentTaskId_StillReturnsLastForRequestedId()
    {
        var output = """
<giant-isopod-result>
{"task_id":"task-other-1","outcome":"completed","summary":"First envelope for different task.","artifacts_expected":["Doc"],"failure_reason":null,"subplan":null}
</giant-isopod-result>

<giant-isopod-result>
{"task_id":"task-target","outcome":"completed","summary":"First envelope for target task.","artifacts_expected":["Doc"],"failure_reason":null,"subplan":null}
</giant-isopod-result>

<giant-isopod-result>
{"task_id":"task-other-2","outcome":"failed","summary":"Second envelope for different task.","artifacts_expected":[],"failure_reason":"Some error.","subplan":null}
</giant-isopod-result>

<giant-isopod-result>
{"task_id":"task-target","outcome":"completed","summary":"Last envelope for target task.","artifacts_expected":["Code"],"failure_reason":null,"subplan":null}
</giant-isopod-result>
""";

        var parsed = StructuredTaskResultParser.Parse(output, "task-target");

        Assert.True(parsed.HasEnvelope);
        Assert.Equal("task-target", parsed.EnvelopeTaskId);
        Assert.Equal(StructuredTaskResultParser.ParsedTaskOutcome.Completed, parsed.Outcome);
        Assert.Equal("Last envelope for target task.", parsed.Summary);
        Assert.Null(parsed.FailureReason);
        Assert.Null(parsed.Subplan);
        Assert.Equal(ArtifactType.Code, Assert.Single(parsed.ExpectedArtifactTypes));
    }

    [Fact]
    public void Parse_IgnoresEnvelopeWithEarlierDifferentTaskIdAndReturnsLastEnvelopeForRequestedTaskId()
    {
        var output = """
<giant-isopod-result>
{"task_id":"task-unrelated","outcome":"completed","summary":"Earlier envelope for different task id.","artifacts_expected":["Doc"],"failure_reason":null,"subplan":null}
</giant-isopod-result>

Proceeding with the correct task...

<giant-isopod-result>
{"task_id":"task-requested","outcome":"completed","summary":"Last envelope for the requested task id.","artifacts_expected":["Code"],"failure_reason":null,"subplan":null}
</giant-isopod-result>
""";

        var parsed = StructuredTaskResultParser.Parse(output, "task-requested");

        Assert.True(parsed.HasEnvelope);
        Assert.Equal("task-requested", parsed.EnvelopeTaskId);
        Assert.Equal(StructuredTaskResultParser.ParsedTaskOutcome.Completed, parsed.Outcome);
        Assert.Equal("Last envelope for the requested task id.", parsed.Summary);
        Assert.Null(parsed.FailureReason);
        Assert.Null(parsed.Subplan);
        Assert.Equal(ArtifactType.Code, Assert.Single(parsed.ExpectedArtifactTypes));
    }

    [Fact]
    public void Parse_IgnoresEarlierEnvelopeForDifferentTaskIdAndReturnsLastEnvelopeForRequestedTaskId()
    {
        var output = """
<giant-isopod-result>
{"task_id":"task-previous","outcome":"completed","summary":"Earlier envelope for different task id.","artifacts_expected":["Doc"],"failure_reason":null,"subplan":null}
</giant-isopod-result>

Processing the requested task...

<giant-isopod-result>
{"task_id":"task-current","outcome":"completed","summary":"Last envelope for the requested task id.","artifacts_expected":["Code"],"failure_reason":null,"subplan":null}
</giant-isopod-result>
""";

        var parsed = StructuredTaskResultParser.Parse(output, "task-current");

        Assert.True(parsed.HasEnvelope);
        Assert.Equal("task-current", parsed.EnvelopeTaskId);
        Assert.Equal(StructuredTaskResultParser.ParsedTaskOutcome.Completed, parsed.Outcome);
        Assert.Equal("Last envelope for the requested task id.", parsed.Summary);
        Assert.Null(parsed.FailureReason);
        Assert.Null(parsed.Subplan);
        Assert.Equal(ArtifactType.Code, Assert.Single(parsed.ExpectedArtifactTypes));
    }

    [Fact]
    public void Parse_IgnoresEarlierEnvelopeForDifferentTaskId_ReturnsLastEnvelopeForRequestedTaskId()
    {
        var output = """
<giant-isopod-result>
{"task_id":"task-wrong","outcome":"completed","summary":"Earlier envelope for different task id.","artifacts_expected":["Doc"],"failure_reason":null,"subplan":null}
</giant-isopod-result>

Working on the actual task...

<giant-isopod-result>
{"task_id":"task-correct","outcome":"completed","summary":"Last envelope for the requested task id.","artifacts_expected":["Code"],"failure_reason":null,"subplan":null}
</giant-isopod-result>
""";

        var parsed = StructuredTaskResultParser.Parse(output, "task-correct");

        Assert.True(parsed.HasEnvelope);
        Assert.Equal("task-correct", parsed.EnvelopeTaskId);
        Assert.Equal(StructuredTaskResultParser.ParsedTaskOutcome.Completed, parsed.Outcome);
        Assert.Equal("Last envelope for the requested task id.", parsed.Summary);
        Assert.Null(parsed.FailureReason);
        Assert.Null(parsed.Subplan);
        Assert.Equal(ArtifactType.Code, Assert.Single(parsed.ExpectedArtifactTypes));
    }

    [Fact]
    public void Parse_IgnoresEarlierDifferentTaskIdEnvelope_ReturnsLastEnvelopeForRequestedTaskId()
    {
        var output = """
<giant-isopod-result>
{"task_id":"task-unrelated","outcome":"completed","summary":"Earlier envelope for unrelated task.","artifacts_expected":["Doc"],"failure_reason":null,"subplan":null}
</giant-isopod-result>

<giant-isopod-result>
{"task_id":"task-requested","outcome":"completed","summary":"Last envelope for the requested task id.","artifacts_expected":["Code"],"failure_reason":null,"subplan":null}
</giant-isopod-result>
""";

        var parsed = StructuredTaskResultParser.Parse(output, "task-requested");

        Assert.True(parsed.HasEnvelope);
        Assert.Equal("task-requested", parsed.EnvelopeTaskId);
        Assert.Equal(StructuredTaskResultParser.ParsedTaskOutcome.Completed, parsed.Outcome);
        Assert.Equal("Last envelope for the requested task id.", parsed.Summary);
        Assert.Null(parsed.FailureReason);
        Assert.Null(parsed.Subplan);
        Assert.Equal(ArtifactType.Code, Assert.Single(parsed.ExpectedArtifactTypes));
    }

    [Fact]
    public void Parse_IgnoresEarlierEnvelopeForDifferentTaskId_StillReturnsLastEnvelopeForRequestedTaskId()
    {
        var output = """
<giant-isopod-result>
{"task_id":"task-other","outcome":"completed","summary":"Earlier envelope for a different task id.","artifacts_expected":["Doc"],"failure_reason":null,"subplan":null}
</giant-isopod-result>

<giant-isopod-result>
{"task_id":"task-expected","outcome":"completed","summary":"Last envelope for the requested task id.","artifacts_expected":["Code"],"failure_reason":null,"subplan":null}
</giant-isopod-result>
""";

        var parsed = StructuredTaskResultParser.Parse(output, "task-expected");

        Assert.True(parsed.HasEnvelope);
        Assert.Equal("task-expected", parsed.EnvelopeTaskId);
        Assert.Equal(StructuredTaskResultParser.ParsedTaskOutcome.Completed, parsed.Outcome);
        Assert.Equal("Last envelope for the requested task id.", parsed.Summary);
        Assert.Null(parsed.FailureReason);
        Assert.Null(parsed.Subplan);
        Assert.Equal(ArtifactType.Code, Assert.Single(parsed.ExpectedArtifactTypes));
    }
}
