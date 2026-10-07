using System.Diagnostics.CodeAnalysis;
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
/// <para>The discarded items were always the oldest unread ones, so the gap they leave lies immediately
/// before the next item the reader takes. The count is therefore moved to that item at the moment it is
/// read, inside the same lock as the read, and <see cref="TakeDropped"/> hands it to the frame writer, which
/// puts the marker on the wire in front of that frame. A discard that happens while the reader is still
/// writing the item it holds belongs to the item after it, not in front of the one being written. Moving
/// the count also ends the episode, so a client that stays slow is reported once per gap rather than once
/// per event. A count the reader has not taken by its next read is carried to that read, never overwritten,
/// so a reader that skips an item without writing it still reports the gap in front of the next one.</para>
/// <para>Every placement, discard and read, and the episode flag, change under one lock. Without it a read
/// could free a slot between a failed write and its discard (throwing away an item that had room), and a
/// count taken between a discard and its episode check reported an episode twice.</para>
/// <para>One writer and one reader at a time.</para>
/// </remarks>
internal sealed class LiveEventBuffer<T>
{
    private readonly Lock _gate = new();

    private readonly Channel<T> _channel;

    private long _droppedSinceLastRead;

    private long _droppedBeforeLastRead;

    private bool _episodeReported;

    private bool _completed;

    public LiveEventBuffer(int capacity)
    {
        _channel = Channel.CreateBounded<T>(
            new BoundedChannelOptions(capacity)
            {
                SingleReader = false,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

        Reader = new CountingReader(this);
    }

    /// <summary>
    /// Reads the retained items oldest first; each read moves the count of items discarded just before it to
    /// <see cref="TakeDropped"/>.
    /// </summary>
    public ChannelReader<T> Reader { get; }

    /// <summary>
    /// Places <paramref name="item"/>, discarding the oldest unread item when the buffer is full.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when this write discarded an item and opened a new episode, which is the moment
    /// to log; otherwise <see langword="false"/>.
    /// </returns>
    public bool Write(T item)
    {
        lock (_gate)
        {
            if (_completed)
            {
                return false;
            }

            if (_channel.Writer.TryWrite(item))
            {
                return false;
            }

            if (!_channel.Reader.TryRead(out _))
            {
                // Full and empty at once cannot happen while the lock is held; refuse rather than spin.
                return false;
            }

            _ = _channel.Writer.TryWrite(item);

            _droppedSinceLastRead++;

            if (_episodeReported)
            {
                return false;
            }

            _episodeReported = true;

            return true;
        }
    }

    /// <summary>
    /// Returns how many items were discarded immediately before the item last read (plus any count an earlier
    /// read moved that was never taken), once.
    /// </summary>
    public long TakeDropped()
    {
        lock (_gate)
        {
            long dropped = _droppedBeforeLastRead;

            _droppedBeforeLastRead = 0;

            return dropped;
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;

            _ = _channel.Writer.TryComplete();
        }
    }

    private bool TryReadAndMoveCount([MaybeNullWhen(false)] out T item)
    {
        lock (_gate)
        {
            if (!_channel.Reader.TryRead(out item))
            {
                return false;
            }

            if (_droppedSinceLastRead > 0)
            {
                _episodeReported = false;
            }

            // Added, not assigned: a reader that skips an item without taking its count (the session
            // stream's drain skips an Entry it already replayed) carries that gap to the next item rather
            // than losing the only count of what the client missed.
            _droppedBeforeLastRead += _droppedSinceLastRead;

            _droppedSinceLastRead = 0;

            return true;
        }
    }

    private sealed class CountingReader(LiveEventBuffer<T> buffer) : ChannelReader<T>
    {
        public override Task Completion => buffer._channel.Reader.Completion;

        public override bool CanCount => buffer._channel.Reader.CanCount;

        public override int Count => buffer._channel.Reader.Count;

        public override bool TryRead([MaybeNullWhen(false)] out T item) => buffer.TryReadAndMoveCount(out item);

        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) =>
            buffer._channel.Reader.WaitToReadAsync(cancellationToken);
    }
}
