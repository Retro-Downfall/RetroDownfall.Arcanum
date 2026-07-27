using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Events;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// AOT-safe in-memory <see cref="IEventBus"/> with bounded per-subscriber channels and DropOldest back-pressure.
/// </summary>
internal sealed class InMemoryEventBus : IEventBus
{

    private readonly ConcurrentDictionary<Type, object> _hubs = new();

    public void Publish<T>(T @event) where T : notnull
    {

        ScryingPool<T> hub = GetOrCreateHub<T>();

        hub.Publish(@event);
    }

    public async IAsyncEnumerable<T> Subscribe<T>(
        [EnumeratorCancellation] CancellationToken cancellationToken) where T : notnull
    {

        ScryingPool<T> hub = GetOrCreateHub<T>();

        ChannelReader<T> reader = hub.Subscribe(out Guid id);

        try
        {

            await foreach (T item in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {

                yield return item;
            }
        }
        finally
        {

            hub.Unsubscribe(id);

        }
    }

    private ScryingPool<T> GetOrCreateHub<T>() where T : notnull
    {

        int capacity = ArcanumSettingClamps.EventBusChannelCapacity(
            ArcanumRuntimeDefaults.EventBus.ChannelCapacity);

        return (ScryingPool<T>)_hubs.GetOrAdd(typeof(T), _ => new ScryingPool<T>(capacity));
    }

}
