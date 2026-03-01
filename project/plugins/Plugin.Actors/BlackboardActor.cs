using Akka.Actor;
using GiantIsopod.Contracts.Core;
using Microsoft.Extensions.Logging;

namespace GiantIsopod.Plugin.Actors;

/// <summary>
/// /user/blackboard — shared key-value memory with EventStream pub/sub.
/// Agents publish signals for cross-agent coordination (stigmergy pattern).
/// </summary>
public sealed class BlackboardActor : UntypedActor
{
    private readonly ILogger<BlackboardActor> _logger;
    private readonly Dictionary<string, (string Value, string? PublisherId)> _signals = new();
    private readonly Dictionary<string, HashSet<IActorRef>> _subscribers = new();

    public BlackboardActor(ILogger<BlackboardActor> logger)
    {
        _logger = logger;
    }

    protected override void OnReceive(object message)
    {
        switch (message)
        {
            case PublishSignal publish:
                _signals[publish.Key] = (publish.Value, publish.PublisherId);
                _logger.LogDebug("Signal published: {Key} by {Publisher}", publish.Key, publish.PublisherId ?? "unknown");

                // Notify direct subscribers
                if (_subscribers.TryGetValue(publish.Key, out var subs))
                {
                    var notification = new SignalValue(publish.Key, publish.Value, publish.PublisherId);
                    foreach (var subscriber in subs)
                        subscriber.Tell(notification);
                }

                // Publish to EventStream for global subscribers
                Context.System.EventStream.Publish(new SignalValue(publish.Key, publish.Value, publish.PublisherId));
                break;

            case QuerySignal query:
                if (_signals.TryGetValue(query.Key, out var entry))
                    Sender.Tell(new SignalValue(query.Key, entry.Value, entry.PublisherId));
                else
                    Sender.Tell(new SignalValue(query.Key, null));
                break;

            case SubscribeSignal subscribe:
                if (!_subscribers.TryGetValue(subscribe.Key, out var set))
                {
                    set = new HashSet<IActorRef>();
                    _subscribers[subscribe.Key] = set;
                }
                set.Add(Sender);
                Context.Watch(Sender);
                _logger.LogDebug("Subscriber added for key {Key}", subscribe.Key);

                // Send current value immediately if it exists
                if (_signals.TryGetValue(subscribe.Key, out var current))
                    Sender.Tell(new SignalValue(subscribe.Key, current.Value, current.PublisherId));
                break;

            case ListSignals list:
                IReadOnlyList<string> keys = list.KeyPrefix is null
                    ? _signals.Keys.ToList()
                    : _signals.Keys.Where(k => k.StartsWith(list.KeyPrefix, StringComparison.Ordinal)).ToList();
                Sender.Tell(new SignalList(keys));
                break;

            case Terminated terminated:
                // Clean up dead subscriber references
                foreach (var subSet in _subscribers.Values)
                    subSet.Remove(terminated.ActorRef);
                break;
        }
    }
}
