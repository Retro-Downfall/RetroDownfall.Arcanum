using System.Threading.Channels;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Fan-out hub for a single event type: one bounded channel per subscriber.
/// </summary>
internal sealed class EventHub<T> where T : notnull
{

    private readonly BoundedChannelOptions _options;

    private readonly Dictionary<Guid, ChannelWriter<T>> _writers = new();

    private readonly Lock _lock = new();

    public EventHub(int capacity)
    {

        _options = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            // Publishers may run on MCP lifecycle threads, daemon jobs, and API callers; the lock below serializes writes per hub.
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        };
    }

    public ChannelReader<T> Subscribe(out Guid subscriptionId)
    {

        Channel<T> channel = Channel.CreateBounded<T>(_options);

        subscriptionId = Guid.NewGuid();

        lock (_lock)
        {

            _writers[subscriptionId] = channel.Writer;
        }

        return channel.Reader;
    }

    public int SubscriberCount
    {

        get
        {

            lock (_lock)
            {

                return _writers.Count;
            }
        }
    }

    /// <summary>
    /// Removes a subscriber. Returns <see langword="true"/> when the last subscriber was removed.
    /// </summary>
    public bool Unsubscribe(Guid subscriptionId)
    {

        lock (_lock)
        {

            if (_writers.Remove(subscriptionId, out ChannelWriter<T>? writer))
            {

                writer.Complete();

                return _writers.Count == 0;
            }

            return false;
        }
    }

    public void Publish(T @event)
    {

        lock (_lock)
        {

            foreach (ChannelWriter<T> writer in _writers.Values)
            {

                _ = writer.TryWrite(@event);
            }
        }
    }

}
