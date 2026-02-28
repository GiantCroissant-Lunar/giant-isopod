using Akka.Actor;
using GiantIsopod.Contracts.Core;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// /user/registry — indexes skills, tracks agent capability sets,
/// resolves dispatch queries against capability requirements.
/// </summary>
public sealed class SkillRegistryActor : UntypedActor
{
    private readonly Dictionary<string, IReadOnlySet<string>> _agentCapabilities = new();

    protected override void OnReceive(object message)
    {
        switch (message)
        {
            case RegisterSkills register:
                _agentCapabilities[register.AgentId] = register.Capabilities;
                break;

            case UnregisterSkills unregister:
                _agentCapabilities.Remove(unregister.AgentId);
                break;

            case QueryCapableAgents query:
                var matches = _agentCapabilities
                    .Where(kv => query.RequiredCapabilities.IsSubsetOf(kv.Value))
                    .Select(kv => kv.Key)
                    .ToList();
                Sender.Tell(new CapableAgentsResult(matches));
                break;
        }
    }
}
