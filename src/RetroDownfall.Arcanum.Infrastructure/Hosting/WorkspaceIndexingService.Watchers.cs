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
                        : NextWakeLocked() - DateTimeOffset.UtcNow;

                    if (wait != Timeout.InfiniteTimeSpan && wait <= TimeSpan.Zero)
                    {
                        // An empty, still-due sweep owns no waiter or execution handle. With entries
                        // registered, due work that was not started yet became due after the sweep
                        // started above, so only a short pause separates it from its start.
                        wait = _entries.Count == 0 ? TimeSpan.FromSeconds(1) : TimeSpan.FromMilliseconds(10);
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
            if (_intakeClosed || _sweep is not null || _entries.Count == 0)
            {
                return;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;

            // A full sweep covers every entry when the reconciliation cadence is due. Otherwise only
            // the failed entries whose own backoff elapsed are retried, so one vanished workspace does
            // not re-walk every healthy one.
            bool fullDue = _nextReconciliation <= now;

            int included = 0;

            foreach (WorkspaceEntry entry in _entries.Values)
            {
                if (fullDue || IsRetryDue(entry, now))
                {
                    included++;
                }
            }

            if (included == 0)
            {
                return;
            }

            int interval = ArcanumSettingClamps.EmbeddingsCodebaseReconciliationIntervalMinutes(embeddings.Codebase.ReconciliationIntervalMinutes);

            _sweep = new ScheduledSweep(included, interval, retryOnly: !fullDue);

            foreach (WorkspaceEntry entry in _entries.Values)
            {
                if (!fullDue && !IsRetryDue(entry, now))
                {
                    continue;
                }

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

    private static bool IsRetryDue(WorkspaceEntry entry, DateTimeOffset now) =>
        entry.RetryAt is { } retryAt && retryAt <= now;

    private void SettleSweepLocked(WorkspaceEntry entry, bool failed = false, bool rootUnavailable = false)
    {
        ScheduledSweep? sweep = entry.Sweep;

        entry.Sweep = null;

        if (sweep is null)
        {
            return;
        }

        if (!entry.Retired)
        {
            RecordSweepOutcomeLocked(entry, sweep, failed, rootUnavailable);
        }

        if (--sweep.Outstanding == 0 && ReferenceEquals(sweep, _sweep))
        {
            _sweep = null;

            // A failure no longer shortens the cadence for everyone: the failed entries carry their own
            // retry time, and a retry-only sweep leaves the full cadence where it was.
            if (!sweep.RetryOnly)
            {
                _nextReconciliation = DateTimeOffset.UtcNow.AddMinutes(sweep.IntervalMinutes);
            }
        }
    }

    /// <summary>
    /// Records how an entry's share of a sweep ended: a success clears its failure state, a failure
    /// schedules its next retry on an exponential backoff capped at the reconciliation interval.
    /// </summary>
    private void RecordSweepOutcomeLocked(WorkspaceEntry entry, ScheduledSweep sweep, bool failed, bool rootUnavailable)
    {
        if (!failed)
        {
            entry.ClearFailureState();

            return;
        }

        entry.ConsecutiveFailures++;

        entry.ConsecutiveRootFailures = rootUnavailable ? entry.ConsecutiveRootFailures + 1 : 0;

        entry.RetryAt = DateTimeOffset.UtcNow + NextRetryDelay(
            entry.ConsecutiveFailures,
            RetryBackoffBaseDelay,
            TimeSpan.FromMinutes(sweep.IntervalMinutes));
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
            if (_intakeClosed || !IsCurrentLocked(entry) || entry.Watcher is not null || IsWatcherBackoffActiveLocked(entry))
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
            if (_intakeClosed || !IsCurrentLocked(entry) || entry.Watcher is not null || IsWatcherBackoffActiveLocked(entry))
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
            HandleWatcherCreationFailure(entry, registration, ex);
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
        // A path the shared eligibility rule rejects (an ignored directory, a dot-prefixed or hidden
        // segment) never enters Pending: queued, it would cost a delete statement per event and count
        // toward the 4,096-path cap that forces a full re-walk, so a build writing thousands of files
        // under obj/ would trigger one. Judged before the scheduler gate is taken.
        bool queueOldPath = change.Kind == WorkspaceFileChangeKind.Renamed
            && change.OldFullPath is not null
            && IsIndexablePath(entry.Path, change.OldFullPath);

        bool queuePath = IsIndexablePath(entry.Path, change.FullPath);

        lock (_schedulerGate)
        {
            if (_intakeClosed || !IsCurrentLocked(entry) || !ReferenceEquals(entry.Watcher, registration))
            {
                return;
            }

            entry.Status.MarkEvent();

            // A watcher that delivers events is healthy, so its next failure starts the ladder over.
            entry.ClearWatcherFailureState();

            if (!queueOldPath && !queuePath)
            {
                return;
            }

            bool overflowed = false;

            if (queueOldPath)
            {
                overflowed |= entry.Pending.Add(change.OldFullPath!, PendingPathAction.Delete);
            }

            if (queuePath)
            {
                overflowed |= entry.Pending.Add(change.FullPath,
                    change.Kind == WorkspaceFileChangeKind.Deleted ? PendingPathAction.Delete : PendingPathAction.Upsert);
            }

            if (overflowed)
            {
                entry.Status.MarkDegraded(overflowed: true);
            }
        }

        SignalWatcherWork();
    }

    /// <summary>
    /// Whether a watcher event's path may be queued: the workspace root itself (a directory event that
    /// still requests reconciliation) or a path whose relative segments pass the lexical half of
    /// <see cref="WorkspaceIndexEligibility"/>. A path that cannot be made relative is dropped.
    /// </summary>
    private static bool IsIndexablePath(string workspacePath, string fullPath)
    {
        try
        {
            string relativePath = Path.GetRelativePath(workspacePath, fullPath);

            return string.Equals(relativePath, ".", StringComparison.Ordinal)
                || WorkspaceIndexEligibility.HasEligibleSegments(relativePath);
        }
        catch (ArgumentException)
        {
            return false;
        }
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

            // A watcher that ran and then failed may have lost events, so what it covered is re-read.
            entry.Pending.RequestForcedReconciliation();

            RecordWatcherFailureLocked(entry);
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

    /// <summary>
    /// A watcher that could not be created delivered no events, so nothing was lost: the entry is only
    /// marked degraded and re-creation backs off. The periodic reconciliation already covers a
    /// workspace nothing watches, and forcing a full re-read per failure would repeat for as long as
    /// the factory keeps failing.
    /// </summary>
    private void HandleWatcherCreationFailure(WorkspaceEntry entry, WatcherRegistration registration, Exception exception)
    {
        TimeSpan retryIn;

        lock (_schedulerGate)
        {
            if (_intakeClosed || !IsCurrentLocked(entry) || !ReferenceEquals(entry.Watcher, registration))
            {
                return;
            }

            _ = RetireWatcherLocked(entry);

            entry.Status.MarkDegraded(overflowed: false);

            RecordWatcherFailureLocked(entry);

            retryIn = entry.WatcherRetryAt is { } retryAt ? retryAt - DateTimeOffset.UtcNow : TimeSpan.Zero;
        }

        retryIn = retryIn > TimeSpan.Zero ? retryIn : TimeSpan.Zero;

        logger.LogWarning(
            exception,
            "Workspace watcher could not be created for {WorkspacePath}; trying again in {RetryDelay}. Periodic reconciliation covers the workspace until then.",
            entry.Path,
            retryIn);

        if (retryIn == TimeSpan.Zero)
        {
            // The one immediate retry has no deadline for the loop to wait on, so wake it.
            SignalWatcherWork();
        }
    }

    /// <summary>
    /// Records a watcher failure and schedules the next creation attempt: the first retry is immediate,
    /// then the delay doubles from <see cref="RetryBackoffBaseDelay"/> up to the reconciliation interval.
    /// </summary>
    private void RecordWatcherFailureLocked(WorkspaceEntry entry)
    {
        entry.WatcherFailures++;

        if (entry.WatcherFailures <= 1)
        {
            entry.WatcherRetryAt = DateTimeOffset.UtcNow;

            return;
        }

        int interval = ArcanumSettingClamps.EmbeddingsCodebaseReconciliationIntervalMinutes(
            optionsMonitor.CurrentValue.ResolveEmbeddings().Codebase.ReconciliationIntervalMinutes);

        entry.WatcherRetryAt = DateTimeOffset.UtcNow + NextRetryDelay(
            entry.WatcherFailures - 1,
            RetryBackoffBaseDelay,
            TimeSpan.FromMinutes(interval));
    }

    private static bool IsWatcherBackoffActiveLocked(WorkspaceEntry entry) =>
        entry.WatcherRetryAt is { } retryAt && retryAt > DateTimeOffset.UtcNow;

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
