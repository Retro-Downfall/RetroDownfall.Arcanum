namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Closes refresh admission and joins the actual work owned by one root or the host.</summary>
internal sealed class GuildSpendRefreshTracker
{
    private readonly object _gate = new();

    private readonly Dictionary<Guid, HashSet<Entry>> _active = [];

    private readonly HashSet<Guid> _closed = [];

    private readonly Dictionary<Guid, int> _scopedClosures = [];

    private bool _stopping;

    internal bool TryEnter(Guid root, out Entry? entry)
    {
        lock (_gate)
        {
            if (_stopping || root == Guid.Empty || (_closed.Contains(root) || _scopedClosures.ContainsKey(root)))
            {
                entry = null;

                return false;
            }

            entry = new(this, root);

            if (!_active.TryGetValue(root, out HashSet<Entry>? entries)) _active[root] = entries = [];

            entries.Add(entry);

            return true;
        }
    }

    internal Guid[] ActiveRoots()
    {
        lock (_gate)
        {
            return [.. _active.Keys];
        }
    }

    internal async Task<IDisposable> CloseRootsScopedAsync(IReadOnlyCollection<Guid> roots)
    {
        Guid[] distinct = roots.Distinct().ToArray();

        if (distinct.Contains(Guid.Empty)) throw new ArgumentOutOfRangeException(nameof(roots));

        Entry[] entries;

        lock (_gate)
        {
            foreach (Guid root in distinct)
                _scopedClosures[root] = _scopedClosures.GetValueOrDefault(root) + 1;

            entries = distinct.Where(_active.ContainsKey).SelectMany(root => _active[root]).ToArray();
        }

        RootClosure scope = new(this, distinct);

        try
        {
            try
            {
                await Task.WhenAll(entries.Select(static entry => entry.CancelAsync())).ConfigureAwait(false);
            }
            finally
            {
                await Task.WhenAll(entries.Select(static entry => entry.Completion)).ConfigureAwait(false);
            }

            return scope;
        }
        catch
        {
            scope.Dispose();

            throw;
        }
    }

    private void ReleaseRoots(Guid[] roots)
    {
        lock (_gate)
        {
            foreach (Guid root in roots)
            {
                int remaining = _scopedClosures[root] - 1;

                if (remaining == 0) _scopedClosures.Remove(root);
                else _scopedClosures[root] = remaining;
            }
        }
    }

    private sealed class RootClosure(GuildSpendRefreshTracker owner, Guid[] roots) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.ReleaseRoots(roots);
        }
    }

    internal Task CloseRootAsync(Guid root, CancellationToken ct)
    {
        Entry[] entries;

        lock (_gate)
        {
            _closed.Add(root);

            entries = _active.TryGetValue(root, out HashSet<Entry>? found) ? [.. found] : [];
        }

        return CancelAndJoinAsync(entries, ct);
    }

    internal Task CloseAllAsync(CancellationToken ct)
    {
        Entry[] entries;

        lock (_gate)
        {
            _stopping = true;

            entries = _active.Values.SelectMany(static group => group).ToArray();
        }

        return CancelAndJoinAsync(entries, ct);
    }

    /// <summary>Signals only attempts bound to this operation; physical completion remains owned by the attempt.</summary>
    internal Task CancelOperationAsync(Guid operationId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(operationId, Guid.Empty);

        Entry[] entries;

        lock (_gate)
        {
            entries = _active.Values.SelectMany(static group => group)
                .Where(entry => entry.OperationId == operationId).ToArray();
        }

        // Cancellation callbacks can re-enter the tracker, so never invoke them under its gate.
        return Task.WhenAll(entries.Select(static entry => entry.CancelAsync()));
    }

    private static async Task CancelAndJoinAsync(Entry[] entries, CancellationToken ct)
    {
        try
        {
            await Task.WhenAll(entries.Select(static entry => entry.CancelAsync())).WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            // Callback failure is not evidence that the physical attempt has stopped.
            await Task.WhenAll(entries.Select(static entry => entry.Completion)).WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private bool IsOpen(Entry entry)
    {
        lock (_gate)
        {
            return !_stopping && !_closed.Contains(entry.Root) && !_scopedClosures.ContainsKey(entry.Root)
                && _active.TryGetValue(entry.Root, out HashSet<Entry>? entries) && entries.Contains(entry);
        }
    }

    private void Complete(Entry entry)
    {
        lock (_gate)
        {
            entry.MarkComplete();

            if (_active.TryGetValue(entry.Root, out HashSet<Entry>? entries))
            {
                entries.Remove(entry);

                if (entries.Count == 0) _active.Remove(entry.Root);
            }
        }
    }

    internal sealed class Entry(GuildSpendRefreshTracker owner, Guid root) : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();

        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _disposed;

        internal Guid Root { get; } = root;

        private Guid? _operationId;

        internal Guid? OperationId => _operationId;

        internal void BindOperation(Guid operationId)
        {
            ArgumentOutOfRangeException.ThrowIfEqual(operationId, Guid.Empty);

            lock (owner._gate)
            {
                if (_operationId is { } existing && existing != operationId)
                    throw new InvalidOperationException("A refresh attempt cannot change its operation identity.");

                _operationId = operationId;
            }
        }

        internal CancellationToken Stop => _stop.Token;

        internal bool IsOpen => owner.IsOpen(this);

        internal Task Completion => _completion.Task;

        internal Task CancelAsync() => _stop.CancelAsync();

        internal void MarkComplete() => _completion.TrySetResult();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Complete(this);
        }
    }
}
