using System.Threading.Channels;

namespace RetroDownfall.Arcanum.Api.Streaming;

/// <summary>
/// The bounded buffer between a live-event subscription and one slow-reading SSE client, which says out
/// loud when it had to throw an event away.
/// </summary>
/// <remarks>
/// <para>A <see cref="BoundedChannelFullMode.DropOldest"/> channel never reports a drop, so a route that
/// put one between its hub subscription and its response made the documented slow-reader marker and log
/// warning unreachable: the pump drained the hub as fast as it could into a buffer that overflowed in
/// silence, and the hub's own channel, where overflow is reported, never filled. This buffer is full when
/// <see cref="Write"/> cannot place an item, and only then does the writer discard the oldest unread item
/// itself, counting it, so every discard is known exactly.</para>
/// <para>The count is taken by the frame writer, which puts the marker on the wire immediately before the
/// next frame it writes: the discarded items were the oldest unread ones, so that is where the gap lies.
/// Taking the count also ends the episode, so a client that stays slow is reported once per count taken
/// rather than once per event.</para>
/// <para>One writer and one reader at a time. The writer also removes the oldest item, which is why the
/// channel does not declare a single reader.</para>
/// </remarks>
internal sealed class LiveEventBuffer<T>
{
    private readonly Channel<T> _channel;

    private long _dropped;

    private int _episodeReported;

    private int _completed;

    public LiveEventBuffer(int capacity)
    {
        _channel = Channel.CreateBounded<T>(
            new BoundedChannelOptions(capacity)
            {
                SingleReader = false,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
    }

    public ChannelReader<T> Reader => _channel.Reader;

    /// <summary>
    /// Places <paramref name="item"/>, discarding the oldest unread item when the buffer is full.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when this write discarded an item and no earlier discard is still waiting to
    /// be reported, which is the moment to log; otherwise <see langword="false"/>.
    /// </returns>
    public bool Write(T item)
    {
        if (Volatile.Read(ref _completed) != 0)
        {
            return false;
        }

        if (_channel.Writer.TryWrite(item))
        {
            return false;
        }

        bool discarded = _channel.Reader.TryRead(out _);

        _ = _channel.Writer.TryWrite(item);

        if (!discarded)
        {
            return false;
        }

        _ = Interlocked.Increment(ref _dropped);

        return Interlocked.CompareExchange(ref _episodeReported, 1, 0) == 0;
    }

    /// <summary>
    /// Returns how many items were discarded since the last call and starts a new episode.
    /// </summary>
    public long TakeDropped()
    {
        long dropped = Interlocked.Exchange(ref _dropped, 0);

        Volatile.Write(ref _episodeReported, 0);

        return dropped;
    }

    public void Complete()
    {
        Volatile.Write(ref _completed, 1);

        _ = _channel.Writer.TryComplete();
    }
}
