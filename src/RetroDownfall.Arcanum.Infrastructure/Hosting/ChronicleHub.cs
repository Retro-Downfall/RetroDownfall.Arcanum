using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Fan-out hub for apprentice Chronicle events, one bounded channel per subscriber per apprentice.
/// </summary>
public sealed class ChronicleHub
{

    private readonly ConcurrentDictionary<Guid, PerApprenticeHub> _hubs = new();

    private readonly Lock _lifecycleLock = new();

    /// <summary>
    /// Number of apprentices holding a live per-apprentice hub. A hub exists exactly while it has at
    /// least one subscriber, so this is never a count of apprentices that have merely published.
    /// </summary>
    internal int TrackedApprenticeCount => _hubs.Count;

    public void Publish(Guid apprenticeId, ApprenticeEvent @event)
    {

        // Publishing must never create a hub: a hub is removed only when its last subscriber leaves,
        // so a publisher-created hub (a headless run, or any event after the operator closed the SSE
        // stream) would live for the lifetime of this singleton. With nobody subscribed the event has
        // no destination anyway — the Chronicle is live-only, never replayed from memory.
        lock (_lifecycleLock)
        {

            if (!_hubs.TryGetValue(apprenticeId, out PerApprenticeHub? hub))
            {

                return;
            }

            int drops = hub.Publish(@event);

            if (drops > 0)
            {

                hub.Publish(new ApprenticeEvent
                {

                    Type = ApprenticeEventType.EventsDropped,

                    ApprenticeId = apprenticeId,

                    Timestamp = DateTimeOffset.UtcNow,

                    Summary = $"{drops} chronicle subscriber(s) dropped events due to slow consumption.",

                });

            }
        }

    }

    public async IAsyncEnumerable<ApprenticeEvent> SubscribeAsync(
        Guid apprenticeId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        PerApprenticeHub hub;

        ChannelReader<ApprenticeEvent> reader;

        Guid subscriptionId;

        lock (_lifecycleLock)
        {
            hub = GetOrCreateHub(apprenticeId);
            reader = hub.Subscribe(out subscriptionId);
        }

        try
        {
            await foreach (ApprenticeEvent item in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {

            lock (_lifecycleLock)
            {

                if (hub.Unsubscribe(subscriptionId))
                {

                    _ = _hubs.TryRemove(apprenticeId, out _);
                }
            }
        }
    }

    private PerApprenticeHub GetOrCreateHub(Guid apprenticeId)
    {
        int capacity = ArcanumSettingClamps.ChronicleChannelCapacity(
            ArcanumRuntimeDefaults.Apprentices.ChronicleChannelCapacity);

        return _hubs.GetOrAdd(apprenticeId, _ => new PerApprenticeHub(capacity));
    }

    private sealed class PerApprenticeHub
    {

        private readonly ScryingPool<ApprenticeEvent> _inner;

        public PerApprenticeHub(int capacity)
        {
            _inner = new ScryingPool<ApprenticeEvent>(capacity);
        }

        public int Publish(ApprenticeEvent @event) => _inner.Publish(@event);

        public ChannelReader<ApprenticeEvent> Subscribe(out Guid subscriptionId) =>
            _inner.Subscribe(out subscriptionId);

        public bool Unsubscribe(Guid subscriptionId) => _inner.Unsubscribe(subscriptionId);

    }

}
