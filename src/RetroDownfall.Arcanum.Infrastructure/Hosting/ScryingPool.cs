using System.Threading.Channels;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Fan-out hub for a single event type: one bounded channel per subscriber.
/// </summary>
internal sealed class ScryingPool<T> where T : notnull
{

    private readonly int _capacity;

    private readonly BoundedChannelOptions _options;

    private readonly Dictionary<Guid, Channel<T>> _channels = new();

    private readonly Lock _lock = new();

    public ScryingPool(int capacity)
    {

        _capacity = capacity;

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

            _channels[subscriptionId] = channel;
        }

        return channel.Reader;
    }

    public int SubscriberCount
    {

        get
        {

            lock (_lock)
            {

                return _channels.Count;
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

            if (_channels.Remove(subscriptionId, out Channel<T>? channel))
            {

                channel.Writer.Complete();

                return _channels.Count == 0;
            }

            return false;
        }
    }

    /// <summary>
    /// Publishes an event to every subscriber. Returns how many subscriber channels dropped an event due to backpressure.
    /// </summary>
    public int Publish(T @event)
    {

        int subscriberDrops = 0;

        lock (_lock)
        {

            foreach (Channel<T> channel in _channels.Values)
            {

                if (channel.Reader.Count >= _capacity)
                {

                    subscriberDrops++;

                }

                _ = channel.Writer.TryWrite(@event);

            }
        }

        return subscriberDrops;

    }

}
