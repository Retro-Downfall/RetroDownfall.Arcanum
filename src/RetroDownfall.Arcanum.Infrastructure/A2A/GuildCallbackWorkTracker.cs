using System.Runtime.ExceptionServices;

namespace RetroDownfall.Arcanum.Infrastructure.A2A;

/// <summary>Owns callback observers independently of request scopes and peer coalescing.</summary>
internal sealed class GuildCallbackWorkTracker(Action<Exception>? reportFailure = null) : IAsyncDisposable
{
    private readonly Lock _gate = new();

    private readonly CancellationTokenSource _shutdown = new();

    private readonly HashSet<Task> _all = [];

    private readonly Dictionary<string, Watch> _current = new(StringComparer.Ordinal);

    private Task? _drain;

    internal Task? Start(string taskId, object peer, Func<CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        lock (_gate)
        {
            if (_drain is not null) return null;

            foreach (Task completed in _all.Where(static task => task.IsCompleted).ToArray())
            {
                if (completed.Exception is { } failed) reportFailure?.Invoke(failed);

                _all.Remove(completed);
            }

            foreach (string key in _current.Where(static entry => entry.Value.Work.IsCompleted).Select(static entry => entry.Key).ToArray())
                _current.Remove(key);

            if (_current.TryGetValue(taskId, out Watch? prior) && ReferenceEquals(prior.Peer, peer)) return prior.Work;

            CancellationToken token = _shutdown.Token;

            Task task = RunWithoutRequestContext(async () =>
            {
                token.ThrowIfCancellationRequested();

                await work(token).ConfigureAwait(false);
            });

            _all.Add(task);

            _current[taskId] = new(peer, task);

            return task;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            // Closing admission and capturing every physical task are one ordering point. A rotated
            // watcher can leave the coalescing index but never the owned set before it completes.
            if (_drain is null)
            {
                Task[] owned = [.. _all];

                _drain = RunWithoutRequestContext(() => DrainAsync(owned));
            }

            return new(_drain);
        }
    }

    private async Task DrainAsync(Task[] tasks)
    {
        Exception? failure = null;

        try
        {
            try
            {
                await _shutdown.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            catch (Exception exception)
            {
                failure = failure is null ? exception : new AggregateException(failure, exception);
            }
        }
        finally
        {
            _shutdown.Dispose();

            lock (_gate)
            {
                _all.Clear();

                _current.Clear();
            }
        }

        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static Task RunWithoutRequestContext(Func<Task> action)
    {
        if (ExecutionContext.IsFlowSuppressed()) return Task.Run(action);

        using (ExecutionContext.SuppressFlow()) return Task.Run(action);
    }

    private sealed record Watch(object Peer, Task Work);
}
