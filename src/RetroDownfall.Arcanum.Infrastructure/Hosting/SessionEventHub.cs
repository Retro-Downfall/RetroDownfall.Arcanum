using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Storage.Entities;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Fan-out hub for session entry events, one bounded channel per subscriber per session.
/// </summary>
public sealed class SessionEventHub
{

    private readonly ConcurrentDictionary<Guid, PerSessionHub> _hubs = new();

    private readonly Lock _lifecycleLock = new();

    private readonly ILogger<SessionEventHub> _logger;

    public SessionEventHub(ILogger<SessionEventHub> logger)
    {
        _logger = logger;
    }

    public void Publish(Guid sessionId, Entry entry)
    {
        PerSessionHub hub = GetOrCreateHub(sessionId);

        int drops = hub.Publish(entry);

        if (drops > 0)
        {

            _logger.LogWarning(
                "Session event hub dropped {Drops} event(s) for session {SessionId} due to slow consumption.",
                drops,
                sessionId);

        }
    }

    public async IAsyncEnumerable<Entry> SubscribeAsync(
        Guid sessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        PerSessionHub hub;

        ChannelReader<Entry> reader;

        Guid subscriptionId;

        lock (_lifecycleLock)
        {
            hub = GetOrCreateHub(sessionId);
            reader = hub.Subscribe(out subscriptionId);
        }

        try
        {
            await foreach (Entry item in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
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

                    _ = _hubs.TryRemove(sessionId, out _);
                }
            }
        }
    }

    private PerSessionHub GetOrCreateHub(Guid sessionId)
    {
        int capacity = ArcanumSettingClamps.ChronicleChannelCapacity(
            ArcanumRuntimeDefaults.Apprentices.ChronicleChannelCapacity);

        return _hubs.GetOrAdd(sessionId, _ => new PerSessionHub(capacity));
    }

    private sealed class PerSessionHub
    {

        private readonly ScryingPool<Entry> _inner;

        public PerSessionHub(int capacity)
        {
            _inner = new ScryingPool<Entry>(capacity);
        }

        public int Publish(Entry entry) => _inner.Publish(entry);

        public ChannelReader<Entry> Subscribe(out Guid subscriptionId) =>
            _inner.Subscribe(out subscriptionId);

        public bool Unsubscribe(Guid subscriptionId) => _inner.Unsubscribe(subscriptionId);

    }

}
