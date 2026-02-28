using Akka.Actor;
using GiantIsopod.Contracts.Core;
using Microsoft.Extensions.Logging;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// /user/registry — indexes skills, tracks agent capability sets,
/// resolves dispatch queries against capability requirements.
/// </summary>
public sealed class SkillRegistryActor : UntypedActor
{
    private readonly ILogger<SkillRegistryActor> _logger;
    private readonly Dictionary<string, IReadOnlySet<string>> _agentCapabilities = new();

    public SkillRegistryActor(ILogger<SkillRegistryActor> logger)
    {
        _logger = logger;
    }

    protected override void OnReceive(object message)
    {
        switch (message)
        {
            case RegisterSkills register:
                _agentCapabilities[register.AgentId] = register.Capabilities;
                _logger.LogDebug("Registered {Count} capabilities for {AgentId}",
                    register.Capabilities.Count, register.AgentId);
                break;

            case UnregisterSkills unregister:
                _agentCapabilities.Remove(unregister.AgentId);
                _logger.LogDebug("Unregistered capabilities for {AgentId}", unregister.AgentId);
                break;

            case QueryCapableAgents query:
                var matches = _agentCapabilities
                    .Where(kv => query.RequiredCapabilities.IsSubsetOf(kv.Value))
                    .Select(kv => kv.Key)
                    .ToList();
                Sender.Tell(new CapableAgentsResult(matches));
                _logger.LogDebug("Query matched {Count} agents", matches.Count);
                break;
        }
    }
}
