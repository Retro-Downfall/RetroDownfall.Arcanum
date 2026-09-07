namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

internal sealed class McpLifecycleAdmission(
    CancellationTokenSource lifetime) : IDisposable
{
    private readonly object _gate = new();

    private TaskCompletionSource _drained = CompletedSignal();

    private Task? _closedDrain;

    private int _active;

    private bool _closed;

    private bool _disposed;

    private AggregateException? _cancellationFailure;

    internal AggregateException? CancellationFailure
    {
        get
        {
            lock (_gate)
            {
                return _cancellationFailure;
            }
        }
    }

    internal bool TryEnter(out IAsyncDisposable? lease)
    {
        lock (_gate)
        {
            if (_disposed || _closed)
            {
                lease = null;

                return false;
            }

            if (_active == 0)
            {
                _drained = NewSignal();
            }

            _active++;

            lease = new Lease(this);

            return true;
        }
    }

    internal Task CloseAndCancel()
    {
        bool cancel = false;

        Task drained;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_closed)
            {
                _closed = true;

                _closedDrain = _drained.Task;

                cancel = true;
            }

            drained = _closedDrain!;
        }

        if (cancel)
        {
            try
            {
                lifetime.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (AggregateException ex)
            {
                lock (_gate)
                {
                    _cancellationFailure ??= ex;
                }
            }
        }

        return drained;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (!_closed || _active != 0)
            {
                throw new InvalidOperationException(
                    "MCP lifecycle admission must be closed and drained before disposal.");
            }

            _disposed = true;
        }
    }

    private void Release()
    {
        TaskCompletionSource? completed = null;

        lock (_gate)
        {
            if (_active <= 0)
            {
                return;
            }

            _active--;

            if (_active == 0)
            {
                completed = _drained;
            }
        }

        completed?.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource CompletedSignal()
    {
        TaskCompletionSource completed = NewSignal();

        completed.TrySetResult();

        return completed;
    }

    private sealed class Lease(
        McpLifecycleAdmission owner) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
