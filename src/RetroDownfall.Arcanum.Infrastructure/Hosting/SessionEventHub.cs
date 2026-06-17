using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Storage.Entities;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

public sealed class SessionEventHub
{

    private readonly ConcurrentDictionary<Guid, PerSessionHub> _hubs = new();

    private readonly IOptionsMonitor<ArcanumSettings> _options;

    public SessionEventHub(IOptionsMonitor<ArcanumSettings> options)
    {
        _options = options;
    }

    public void Publish(Guid sessionId, Entry entry)
    {
        PerSessionHub hub = GetOrCreateHub(sessionId);

        hub.Publish(entry);
    }

    public async IAsyncEnumerable<Entry> SubscribeAsync(
        Guid sessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        PerSessionHub hub = GetOrCreateHub(sessionId);

        ChannelReader<Entry> reader = hub.Subscribe(out Guid subscriptionId);

        try
        {
            await foreach (Entry item in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {

            if (hub.Unsubscribe(subscriptionId))
            {

                _ = _hubs.TryRemove(sessionId, out _);
            }
        }
    }

    private PerSessionHub GetOrCreateHub(Guid sessionId)
    {
        int capacity = ArcanumSettingClamps.ChronicleChannelCapacity(
            _options.CurrentValue.Apprentices?.ChronicleChannelCapacity ?? new ApprenticeSettings().ChronicleChannelCapacity);

        return _hubs.GetOrAdd(sessionId, _ => new PerSessionHub(capacity));
    }

    private sealed class PerSessionHub
    {

        private readonly EventHub<Entry> _inner;

        public PerSessionHub(int capacity)
        {
            _inner = new EventHub<Entry>(capacity);
        }

        public void Publish(Entry entry) => _inner.Publish(entry);

        public ChannelReader<Entry> Subscribe(out Guid subscriptionId) =>
            _inner.Subscribe(out subscriptionId);

        public bool Unsubscribe(Guid subscriptionId) => _inner.Unsubscribe(subscriptionId);

    }

}
