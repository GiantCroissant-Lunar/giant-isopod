namespace GiantIsopod.Contracts.Core;

/// <summary>
/// Indexes loaded skills, tracks agent capability sets,
/// and resolves dispatch queries against capability requirements.
/// </summary>
public interface ISkillRegistry
{
    IReadOnlyList<string> GetAgentCapabilities(string agentId);
    IReadOnlyList<string> FindAgentsByCapabilities(IReadOnlySet<string> requiredCapabilities);
    void RegisterAgent(string agentId, IReadOnlySet<string> capabilities);
    void UnregisterAgent(string agentId);
}
