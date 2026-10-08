using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// Buffers streaming token UI refresh signals and flushes them on a short cadence, before non-token
/// blocks, on final/cancel, on dispose, and when the stream goes quiet with a chunk still held back
/// (<see cref="WithTrailingFlushAsync"/>). A newline does not bypass the cadence: every flush copies
/// the whole answer and re-wraps its entry, so flushing per line made a line-heavy answer cost one
/// rebuild per line.
/// Never mutates Terminal.Gui controls — only writes to a UI channel.
/// </summary>
internal sealed class StreamingUiCoalescer : IAsyncDisposable
{
    public static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromMilliseconds(50);

    private readonly ChannelWriter<CommandCenterUiUpdate> _ui;

    private readonly TimeSpan _flushInterval;

    private readonly Func<DateTimeOffset> _utcNow;

    private readonly Action? _beforeFlush;

    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private readonly object _gate = new();
    private bool _dirty;

    private DateTimeOffset _lastFlushUtc;

    private bool _disposed;

    public StreamingUiCoalescer(
        ChannelWriter<CommandCenterUiUpdate> uiUpdates,
        TimeSpan? flushInterval = null,
        Func<DateTimeOffset>? utcNow = null,
        Action? beforeFlush = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _ui = uiUpdates ?? throw new ArgumentNullException(nameof(uiUpdates));
        _flushInterval = flushInterval ?? DefaultFlushInterval;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _beforeFlush = beforeFlush;
        _delay = delay ?? Task.Delay;
        _lastFlushUtc = _utcNow();
    }

    /// <summary>Number of flushes performed (for tests).</summary>
    public int FlushCount { get; private set; }

    /// <summary>True when a token update is pending and not yet flushed.</summary>
    public bool HasPending
    {
        get
        {
            lock (_gate)
            {
                return _dirty;
            }
        }
    }

    /// <summary>
    /// Notes that a token chunk arrived. Flushes when the flush interval has elapsed since the last flush; otherwise
    /// the chunk stays pending until the next chunk, block, or final flush.
    /// </summary>
    public ValueTask NoteTokenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        bool flushNow;
        lock (_gate)
        {
            _dirty = true;
            TimeSpan since = _utcNow() - _lastFlushUtc;
            flushNow = since >= _flushInterval;
            if (flushNow)
            {
                _dirty = false;
                _lastFlushUtc = _utcNow();
            }
        }

        return flushNow
            ? FlushCoreAsync(cancellationToken)
            : ValueTask.CompletedTask;
    }

    /// <summary>Flushes any pending token UI before a tool/status/error (or other) block.</summary>
    public ValueTask FlushBeforeBlockAsync(CancellationToken cancellationToken = default) =>
        FlushPendingAsync(cancellationToken);

    /// <summary>Flushes any pending tokens (final result path).</summary>
    public ValueTask FlushFinalAsync(CancellationToken cancellationToken = default) =>
        FlushPendingAsync(cancellationToken);

    /// <summary>
    /// Flushes any pending tokens after a cancellation. This flush is what puts the cut-off text on the
    /// screen, and it runs because the turn's token was cancelled, so no token may be allowed to abandon
    /// it: it takes none, and the write is made on <see cref="CancellationToken.None"/> (the UI channel is
    /// unbounded, so it completes at once).
    /// </summary>
    public ValueTask FlushCancelledAsync() =>
        FlushPendingAsync(CancellationToken.None);

    /// <summary>
    /// Passes <paramref name="source"/> through unchanged, and flushes a pending chunk when the wait for
    /// the next item outlasts what is left of the flush interval. The cadence alone only flushes on the
    /// next chunk, block or final flush, so a stream that goes quiet right after a burst would leave its
    /// last words off the screen for as long as it stayed quiet.
    /// </summary>
    /// <remarks>
    /// The flush runs while the consumer is awaiting the next item, never while it is handling one, so
    /// the consumer's buffers are never read from two threads at once.
    /// </remarks>
    public async IAsyncEnumerable<T> WithTrailingFlushAsync<T>(
        IAsyncEnumerable<T> source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        // The read is cancelled through its own source as well as the caller's token, so a wait that is
        // abandoned for any reason can end the outstanding read instead of waiting for the next item.
        using CancellationTokenSource enumeration = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using IAsyncEnumerator<T> enumerator = source.GetAsyncEnumerator(enumeration.Token);

        while (true)
        {
            Task<bool> next = enumerator.MoveNextAsync().AsTask();

            try
            {
                await FlushWhileWaitingAsync(next, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // The wait is being abandoned with the read still outstanding. Disposing an async iterator
                // while its MoveNextAsync is running throws, and that would replace the exception that
                // matters, so end the read and let it finish first. Its own outcome is only observed.
                await enumeration.CancelAsync().ConfigureAwait(false);
                _ = await Task.WhenAny(next).ConfigureAwait(false);
                _ = next.Exception;

                throw;
            }

            if (!await next.ConfigureAwait(false))
            {
                yield break;
            }

            yield return enumerator.Current;
        }
    }

    private async Task FlushWhileWaitingAsync(Task<bool> next, CancellationToken cancellationToken)
    {
        if (next.IsCompleted || TimeUntilPendingIsDue() is not { } remaining)
        {
            return;
        }

        using CancellationTokenSource timerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task timer = _delay(remaining, timerCancellation.Token);

        if (await Task.WhenAny(next, timer).ConfigureAwait(false) == next)
        {
            await timerCancellation.CancelAsync().ConfigureAwait(false);
            return;
        }

        // The wait for the next item outlasted the interval. A cancelled token surfaces here as the timer's
        // own cancellation, and nothing is flushed on its behalf: the caller's cancelled-turn path does that.
        await timer.ConfigureAwait(false);
        await FlushPendingAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>How long until a held-back chunk is due, or <see langword="null"/> when none is held.</summary>
    private TimeSpan? TimeUntilPendingIsDue()
    {
        lock (_gate)
        {
            if (!_dirty)
            {
                return null;
            }

            TimeSpan remaining = _flushInterval - (_utcNow() - _lastFlushUtc);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await FlushPendingAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async ValueTask FlushPendingAsync(CancellationToken cancellationToken)
    {
        bool shouldFlush;
        lock (_gate)
        {
            shouldFlush = _dirty;
            if (shouldFlush)
            {
                _dirty = false;
                _lastFlushUtc = _utcNow();
            }
        }

        if (shouldFlush)
        {
            await FlushCoreAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask FlushCoreAsync(CancellationToken cancellationToken)
    {
        _beforeFlush?.Invoke();
        FlushCount++;
        await _ui.WriteAsync(new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshLog), cancellationToken)
            .ConfigureAwait(false);
    }
}
