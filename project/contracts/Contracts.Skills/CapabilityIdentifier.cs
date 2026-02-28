namespace GiantIsopod.Contracts.Skills;

/// <summary>
/// Well-known capability identifiers. Atomic tags declared in skill metadata.
/// </summary>
public static class CapabilityIdentifier
{
    public const string CodeEdit = "code_edit";
    public const string RepoSearch = "repo_search";
    public const string ShellRun = "shell_run";
    public const string TestRun = "test_run";
    public const string PatchEmit = "patch_emit";
    public const string PolicyStrict = "policy_strict";
    public const string VerifyBuild = "verify_build";
    public const string ContextRetrieve = "context_retrieve";
    public const string ConsensusVote = "consensus_vote";
    public const string PlanGenerate = "plan_generate";
    public const string TaskDecompose = "task_decompose";

    private static readonly HashSet<string> WellKnown = new(StringComparer.OrdinalIgnoreCase)
    {
        CodeEdit, RepoSearch, ShellRun, TestRun, PatchEmit,
        PolicyStrict, VerifyBuild, ContextRetrieve, ConsensusVote,
        PlanGenerate, TaskDecompose
    };

    public static bool IsWellKnown(string id) => WellKnown.Contains(id);

    public static void Register(string id) => WellKnown.Add(id);
}
