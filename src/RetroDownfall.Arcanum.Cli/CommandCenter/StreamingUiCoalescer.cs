using System.Threading.Channels;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// Buffers streaming token UI refresh signals and flushes them on a short cadence,
/// newlines, before non-token blocks, final/cancel, and dispose.
/// Never mutates Terminal.Gui controls — only writes to a UI channel.
/// </summary>
internal sealed class StreamingUiCoalescer : IAsyncDisposable
{
    public static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromMilliseconds(50);

    private readonly ChannelWriter<CommandCenterUiUpdate> _ui;

    private readonly TimeSpan _flushInterval;

    private readonly Func<DateTimeOffset> _utcNow;

    private readonly Action? _beforeFlush;
    private readonly object _gate = new();
    private bool _dirty;

    private DateTimeOffset _lastFlushUtc;

    private bool _disposed;

    public StreamingUiCoalescer(
        ChannelWriter<CommandCenterUiUpdate> uiUpdates,
        TimeSpan? flushInterval = null,
        Func<DateTimeOffset>? utcNow = null,
        Action? beforeFlush = null)
    {
        _ui = uiUpdates ?? throw new ArgumentNullException(nameof(uiUpdates));
        _flushInterval = flushInterval ?? DefaultFlushInterval;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _beforeFlush = beforeFlush;
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
    /// Notes a token chunk. Flushes immediately on newline or when the flush interval elapsed.
    /// </summary>
    public ValueTask NoteTokenAsync(string chunk, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        bool flushNow;
        lock (_gate)
        {
            _dirty = true;
            bool hasNewline = chunk.Contains('\n', StringComparison.Ordinal);
            TimeSpan since = _utcNow() - _lastFlushUtc;
            flushNow = hasNewline || since >= _flushInterval;
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

    /// <summary>Flushes any pending tokens on cancellation.</summary>
    public ValueTask FlushCancelledAsync(CancellationToken cancellationToken = default) =>
        FlushPendingAsync(cancellationToken);

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
