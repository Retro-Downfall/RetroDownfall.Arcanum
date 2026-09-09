using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal sealed partial class WorkspaceIndexingService
{
    private readonly SemaphoreSlim _watcherSignal = new(0, 1);

    private int _watcherCount;

    private int _watcherCreations;

    private TaskCompletionSource? _watcherCreationDrain;

    private Task? _stopTask;

    private int _disposed;

    internal int ActiveWatcherCount
    {
        get
        {
            lock (_schedulerGate)
            {
                return _watcherCount;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                EmbeddingSettings embeddings = optionsMonitor.CurrentValue.ResolveEmbeddings();

                if (!embeddings.Enabled || !embeddings.CodebaseRetrievalEnabled)
                {
                    DisposeAllWatchers();

                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);

                    continue;
                }

                WorkspaceEntry[] entries;

                lock (_schedulerGate)
                {
                    if (_intakeClosed)
                    {
                        return;
                    }

                    entries = _entries.Values.ToArray();
                }

                foreach (WorkspaceEntry entry in entries)
                {
                    EnsureWatcher(entry);
                }

                StartScheduledSweep(embeddings);

                TimeSpan wait;

                lock (_schedulerGate)
                {
                    wait = _sweep is not null
                        ? Timeout.InfiniteTimeSpan
                        : _nextReconciliation - DateTimeOffset.UtcNow;

                    if (wait != Timeout.InfiniteTimeSpan && wait <= TimeSpan.Zero)
                    {
                        // An empty, still-due sweep owns no waiter or execution handle.
                        wait = TimeSpan.FromSeconds(1);
                    }
                }

                if (await _watcherSignal.WaitAsync(wait, stoppingToken).ConfigureAwait(false))
                {
                    int debounce = ArcanumSettingClamps.EmbeddingsCodebaseWatcherDebounceMilliseconds(embeddings.Codebase.WatcherDebounceMilliseconds);

                    await Task.Delay(TimeSpan.FromMilliseconds(debounce), stoppingToken).ConfigureAwait(false);
                }

                lock (_schedulerGate)
                {
                    entries = _entries.Values.Where(static entry => entry.Pending.HasDemand).ToArray();
                }

                foreach (WorkspaceEntry entry in entries)
                {
                    WorkspaceHandle? start;

                    lock (_schedulerGate)
                    {
                        start = ScheduleLocked(entry);
                    }

                    StartHandle(start);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Workspace indexing scheduler tick failed; continuing.");

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private void StartScheduledSweep(EmbeddingSettings embeddings)
    {
        WorkspaceHandle? first = null;

        WorkspaceHandle? second = null;

        lock (_schedulerGate)
        {
            if (_intakeClosed || _sweep is not null || _nextReconciliation > DateTimeOffset.UtcNow || _entries.Count == 0)
            {
                return;
            }

            int interval = ArcanumSettingClamps.EmbeddingsCodebaseReconciliationIntervalMinutes(embeddings.Codebase.ReconciliationIntervalMinutes);

            _sweep = new ScheduledSweep(_entries.Count, interval);

            foreach (WorkspaceEntry entry in _entries.Values)
            {
                entry.Sweep = _sweep;

                entry.Pending.Full = true;

                WorkspaceHandle? start = ScheduleLocked(entry);

                if (start is not null)
                {
                    if (first is null)
                    {
                        first = start;
                    }
                    else
                    {
                        second = start;
                    }
                }
            }
        }

        StartHandle(first);

        StartHandle(second);
    }

    private void SettleSweepLocked(WorkspaceEntry entry, bool failed = false)
    {
        ScheduledSweep? sweep = entry.Sweep;

        entry.Sweep = null;

        if (sweep is not null)
        {
            sweep.Failed |= failed;
        }

        if (sweep is not null && --sweep.Outstanding == 0 && ReferenceEquals(sweep, _sweep))
        {
            _sweep = null;

            _nextReconciliation = sweep.Failed
                ? DateTimeOffset.UtcNow.AddSeconds(1)
                : DateTimeOffset.UtcNow.AddMinutes(sweep.IntervalMinutes);
        }
    }

    private void EnsureWatcher(WorkspaceEntry entry)
    {
        EmbeddingSettings embeddings = optionsMonitor.CurrentValue.ResolveEmbeddings();

        if (!embeddings.Enabled || !embeddings.CodebaseRetrievalEnabled)
        {
            return;
        }

        lock (_schedulerGate)
        {
            if (_intakeClosed || !IsCurrentLocked(entry) || entry.Watcher is not null)
            {
                return;
            }

            int maximum = ArcanumSettingClamps.EmbeddingsCodebaseMaxWatchers(embeddings.Codebase.MaxWatchers);

            if (_watcherCount >= maximum)
            {
                entry.Status.MarkDegraded(overflowed: false);

                return;
            }
        }

        if (!_workAdmission.TryAcquireWorkLease(
                GrimoireWorkKind.WorkspaceIndexing,
                out IGrimoireWorkLease? admitted))
        {
            return;
        }

        IGrimoireWorkLease workLease = admitted!;

        try
        {
            EnsureWatcherUnderLease(entry, embeddings);
        }
        finally
        {
            ValueTask disposal = workLease.DisposeAsync();

            if (!disposal.IsCompletedSuccessfully)
            {
                disposal.AsTask().GetAwaiter().GetResult();
            }
        }
    }

    private void EnsureWatcherUnderLease(WorkspaceEntry entry, EmbeddingSettings embeddings)
    {
        WatcherRegistration registration;

        lock (_schedulerGate)
        {
            if (_intakeClosed || !IsCurrentLocked(entry) || entry.Watcher is not null)
            {
                return;
            }

            int maximum = ArcanumSettingClamps.EmbeddingsCodebaseMaxWatchers(embeddings.Codebase.MaxWatchers);

            if (_watcherCount >= maximum)
            {
                entry.Status.MarkDegraded(overflowed: false);

                return;
            }

            registration = new WatcherRegistration();

            entry.Watcher = registration;

            _watcherCount++;

            _watcherCreations++;
        }

        IWorkspaceFileWatcher? created = null;

        try
        {
            created = watcherFactory.Create(entry.Path,
                change => QueueWatcherChange(entry, registration, change),
                exception => HandleWatcherError(entry, registration, exception));

            lock (_schedulerGate)
            {
                if (!_intakeClosed && IsCurrentLocked(entry) && ReferenceEquals(entry.Watcher, registration))
                {
                    registration.Watcher = created;

                    created = null;

                    entry.Status.SetWatching(true);
                }
            }
        }
        catch (Exception ex)
        {
            HandleWatcherError(entry, registration, ex);
        }
        finally
        {
            try
            {
                created?.Dispose();
            }
            finally
            {
                lock (_schedulerGate)
                {
                    if (--_watcherCreations == 0)
                    {
                        _watcherCreationDrain?.TrySetResult();
                    }
                }
            }
        }
    }

    private void QueueWatcherChange(WorkspaceEntry entry, WatcherRegistration registration, WorkspaceFileChange change)
    {
        lock (_schedulerGate)
        {
            if (_intakeClosed || !IsCurrentLocked(entry) || !ReferenceEquals(entry.Watcher, registration))
            {
                return;
            }

            entry.Status.MarkEvent();

            bool overflowed = false;

            if (change.Kind == WorkspaceFileChangeKind.Renamed && change.OldFullPath is not null)
            {
                overflowed |= entry.Pending.Add(change.OldFullPath, PendingPathAction.Delete);
            }

            overflowed |= entry.Pending.Add(change.FullPath,
                change.Kind == WorkspaceFileChangeKind.Deleted ? PendingPathAction.Delete : PendingPathAction.Upsert);

            if (overflowed)
            {
                entry.Status.MarkDegraded(overflowed: true);
            }
        }

        SignalWatcherWork();
    }

    private void HandleWatcherError(WorkspaceEntry entry, WatcherRegistration registration, Exception exception)
    {
        IWorkspaceFileWatcher? watcher;

        lock (_schedulerGate)
        {
            if (_intakeClosed || !IsCurrentLocked(entry) || !ReferenceEquals(entry.Watcher, registration))
            {
                return;
            }

            watcher = RetireWatcherLocked(entry);

            entry.Status.MarkDegraded(overflowed: exception is InternalBufferOverflowException);

            entry.Pending.RequestForcedReconciliation();
        }

        try
        {
            watcher?.Dispose();
        }
        finally
        {
            logger.LogWarning(exception, "Workspace watcher failed for {WorkspacePath}; reconciliation remains pending.", entry.Path);

            SignalWatcherWork();
        }
    }

    private IWorkspaceFileWatcher? RetireWatcherLocked(WorkspaceEntry entry)
    {
        WatcherRegistration? registration = entry.Watcher;

        entry.Watcher = null;

        entry.Status.SetWatching(false);

        if (registration is not null)
        {
            _watcherCount--;
        }

        return registration?.Watcher;
    }

    private void DisposeAllWatchers()
    {
        List<IWorkspaceFileWatcher> watchers = [];

        lock (_schedulerGate)
        {
            foreach (WorkspaceEntry entry in _entries.Values)
            {
                if (RetireWatcherLocked(entry) is { } watcher)
                {
                    watchers.Add(watcher);
                }
            }
        }

        List<Exception>? failures = null;

        foreach (IWorkspaceFileWatcher watcher in watchers)
        {
            try
            {
                watcher.Dispose();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("Workspace watcher disposal failed.", failures);
        }
    }

    private void SignalWatcherWork()
    {
        try
        {
            _watcherSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // The single coalesced wake-up is already pending.
        }
        catch (ObjectDisposedException)
        {
            // A callback that raced final disposal cannot resurrect intake.
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource? owner = null;

        Task[] handles = [];

        Task creations = Task.CompletedTask;

        Task stop;

        lock (_schedulerGate)
        {
            if (_stopTask is null)
            {
                _intakeClosed = true;

                owner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                _stopTask = owner.Task;

                handles = _active.Values.Select(static handle => handle.Terminal.Task).ToArray();

                foreach (WorkspaceEntry entry in _entries.Values)
                {
                    entry.Retired = true;

                    entry.Pending = new WorkspaceDemand();

                    UnlinkLocked(entry);
                }

                if (_watcherCreations != 0)
                {
                    _watcherCreationDrain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                    creations = _watcherCreationDrain.Task;
                }
            }

            stop = _stopTask;
        }

        if (owner is not null)
        {
            _ = CompleteStopAsync(owner, handles, creations);
        }

        // A canceled caller cannot abandon producer-owned scopes, file compensation or watchers.
        return stop;
    }

    private async Task CompleteStopAsync(TaskCompletionSource owner, Task[] handles, Task creations)
    {
        List<Exception>? failures = null;

        try
        {
            _shutdown.Cancel();
        }
        catch (Exception ex)
        {
            (failures ??= []).Add(ex);
        }

        try
        {
            await base.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            (failures ??= []).Add(ex);
        }

        try
        {
            await Task.WhenAll(handles).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            (failures ??= []).Add(ex);
        }

        try
        {
            await creations.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            (failures ??= []).Add(ex);
        }

        try
        {
            DisposeAllWatchers();
        }
        catch (Exception ex)
        {
            (failures ??= []).Add(ex);
        }

        if (failures is null)
        {
            owner.TrySetResult();
        }
        else
        {
            owner.TrySetException(failures);
        }
    }

    public override void Dispose()
    {
        try
        {
            StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            DisposeResources();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            DisposeResources();
        }
    }

    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _shutdown.Dispose();

            _watcherSignal.Dispose();

            base.Dispose();
        }
    }

    private sealed class WatcherRegistration
    {
        internal IWorkspaceFileWatcher? Watcher { get; set; }
    }
}
