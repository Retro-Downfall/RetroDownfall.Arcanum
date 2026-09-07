using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal sealed partial class WorkspaceIndexingService
{
    private const int WorkspaceCapacity = 2;

    private readonly object _schedulerGate = new();

    private readonly Dictionary<string, WorkspaceEntry> _entries = new(CanonicalDirectoryComparer);

    private readonly Dictionary<string, WorkspaceHandle> _active = new(CanonicalDirectoryComparer);

    private readonly CancellationTokenSource _shutdown = new();

    private OverflowFifo? _overflow;

    private bool _intakeClosed;

    private ScheduledSweep? _sweep;

    private DateTimeOffset _nextReconciliation = DateTimeOffset.UtcNow;

    internal Action? BeforeHandleDispatchForTests { get; set; }

    internal WorkspaceSchedulerSnapshot GetSchedulerSnapshot()
    {
        lock (_schedulerGate)
        {
            return new WorkspaceSchedulerSnapshot(_active.Count, _overflow?.Count ?? 0, _overflow is not null);
        }
    }

    internal (int Outstanding, DateTimeOffset NextReconciliation) GetScheduledSweepSnapshot()
    {
        lock (_schedulerGate)
        {
            return (_sweep?.Outstanding ?? 0, _nextReconciliation);
        }
    }

    public WorkspaceIndexRuntimeStatus GetRuntimeStatus(string workspacePath)
    {
        try
        {
            string key = SchedulerKey(Path.GetFullPath(workspacePath));

            lock (_schedulerGate)
            {
                return _entries.TryGetValue(key, out WorkspaceEntry? entry)
                    ? entry.Status.Snapshot()
                    : WorkspaceIndexRuntimeStatus.NotWatching;
            }
        }
        catch (Exception)
        {
            return WorkspaceIndexRuntimeStatus.NotWatching;
        }
    }

    public void RegisterWorkspace(string workspacePath)
    {
        Result<WorkspaceEntry> registered = RegisterEntry(workspacePath);

        if (registered.IsSuccess)
        {
            EnsureWatcher(registered.Value);
        }
    }

    public void UnregisterWorkspace(string workspacePath)
    {
        string key;

        try
        {
            key = SchedulerKey(Path.GetFullPath(workspacePath));
        }
        catch (Exception)
        {
            return;
        }

        WorkspaceHandle? handle;

        IWorkspaceFileWatcher? watcher;

        lock (_schedulerGate)
        {
            if (!_entries.Remove(key, out WorkspaceEntry? entry))
            {
                return;
            }

            entry.Retired = true;

            entry.Pending = new WorkspaceDemand();

            UnlinkLocked(entry);

            handle = entry.Handle;

            watcher = RetireWatcherLocked(entry);

            if (handle is null)
            {
                SettleSweepLocked(entry);
            }
        }

        try
        {
            watcher?.Dispose();
        }
        finally
        {
            handle?.Cancel();
        }
    }

    public Result<WorkspaceIndexQueueDisposition> QueueIndexNow(string workspacePath)
    {
        Result<WorkspaceEntry> registered = RegisterEntry(workspacePath);

        if (registered.IsFailure)
        {
            return Result<WorkspaceIndexQueueDisposition>.Failure(registered.Error);
        }

        WorkspaceEntry entry = registered.Value;

        EnsureWatcher(entry);

        WorkspaceHandle? start;

        WorkspaceIndexQueueDisposition disposition;

        lock (_schedulerGate)
        {
            if (_intakeClosed || !IsCurrentLocked(entry))
            {
                return Unavailable();
            }

            disposition = entry.Handle is not null || entry.Queued || entry.Pending.HasDemand
                ? WorkspaceIndexQueueDisposition.Coalesced
                : WorkspaceIndexQueueDisposition.Accepted;

            entry.Pending.Full = true;

            start = ScheduleLocked(entry);
        }

        return StartHandle(start)
            ? Result<WorkspaceIndexQueueDisposition>.Success(disposition)
            : Unavailable();
    }

    internal void QueuePendingWatcherEvents(string workspacePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        WorkspaceHandle? start = null;

        lock (_schedulerGate)
        {
            if (!_intakeClosed
                && _entries.TryGetValue(SchedulerKey(Path.GetFullPath(workspacePath)), out WorkspaceEntry? entry)
                && entry.Pending.HasDemand)
            {
                start = ScheduleLocked(entry);
            }
        }

        StartHandle(start);
    }

    private Result<WorkspaceEntry> RegisterEntry(string workspacePath)
    {
        string? existingKey = null;

        if (!string.IsNullOrWhiteSpace(workspacePath))
        {
            try
            {
                existingKey = SchedulerKey(Path.GetFullPath(workspacePath.Trim()));
            }
            catch (Exception)
            {
                // The policy validator below returns the stable public path error.
            }
        }

        lock (_schedulerGate)
        {
            if (_intakeClosed)
            {
                return Result<WorkspaceEntry>.Failure(UnavailableError());
            }

            if (existingKey is not null
                && _entries.TryGetValue(existingKey, out WorkspaceEntry? existing)
                && IsCurrentLocked(existing))
            {
                // Wizard registers its working directory on every inference turn. Once this
                // identity is live, avoid repeating directory, allowlist and file-handle I/O;
                // the owned execution revalidates both policy and identity before any effect.
                return Result<WorkspaceEntry>.Success(existing);
            }
        }

        Result<string> validated = CampaignPathPolicy.ValidateAndNormalizePath(workspacePath, optionsMonitor.CurrentValue);

        if (validated.IsFailure)
        {
            return Result<WorkspaceEntry>.Failure(validated.Error);
        }

        if (!FileHandleIdentityInterop.TryGetPathIdentity(validated.Value, out FileHandleIdentity identity))
        {
            return Result<WorkspaceEntry>.Failure(new Error(ErrorCodes.Campaign.InvalidPath, "The workspace directory identity is unavailable."));
        }

        string key = SchedulerKey(validated.Value);

        lock (_schedulerGate)
        {
            if (_intakeClosed)
            {
                return Result<WorkspaceEntry>.Failure(UnavailableError());
            }

            if (!_entries.TryGetValue(key, out WorkspaceEntry? entry))
            {
                entry = new WorkspaceEntry(key, validated.Value, identity);

                _entries.Add(key, entry);
            }

            return Result<WorkspaceEntry>.Success(entry);
        }
    }

    private WorkspaceHandle? ScheduleLocked(WorkspaceEntry entry)
    {
        if (_intakeClosed || !IsCurrentLocked(entry) || entry.Handle is not null || entry.Queued || _active.ContainsKey(entry.Key))
        {
            return null;
        }

        if (_active.Count < WorkspaceCapacity && !_active.ContainsKey(entry.Key) && _overflow is null)
        {
            return PublishHandleLocked(entry);
        }

        EnqueueLocked(entry);

        return PromoteLocked();
    }

    private WorkspaceHandle PublishHandleLocked(WorkspaceEntry entry)
    {
        WorkspaceHandle handle = new(entry, _shutdown.Token);

        entry.Handle = handle;

        _active.Add(entry.Key, handle);

        return handle;
    }

    private bool StartHandle(WorkspaceHandle? handle)
    {
        if (handle is null)
        {
            return true;
        }

        try
        {
            BeforeHandleDispatchForTests?.Invoke();

            // The terminal promise is already published. Never give Task.Run a cancelable token:
            // its body must run the ownership cleanup even when Stop wins before dispatch.
            _ = Task.Run(() => RunHandleAsync(handle));

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Workspace indexing could not start its owned execution.");

            _ = StopAsync(CancellationToken.None);

            FinishHandle(handle);

            return false;
        }
    }

    private async Task RunHandleAsync(WorkspaceHandle handle)
    {
        WorkspaceEntry entry = handle.Entry;

        CancellationToken cancellationToken = handle.Cancellation.Token;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                long generation = _workAdmission.CurrentGeneration;

                WorkspaceUnitOutcome outcome = WorkspaceUnitOutcome.Deferred;

                if (_workAdmission.TryAcquireWorkLease(GrimoireWorkKind.WorkspaceIndexing, out IGrimoireWorkLease? admitted))
                {
                    await using IGrimoireWorkLease workLease = admitted!;

                    generation = workLease.Generation;

                    WorkspaceDemand demand;

                    lock (_schedulerGate)
                    {
                        if (!IsCurrentLocked(entry))
                        {
                            return;
                        }

                        demand = entry.Pending;

                        entry.Pending = new WorkspaceDemand();

                        if (demand.Full)
                        {
                            handle.Sweep = entry.Sweep;
                        }

                        entry.Status.SetReconciling(true);
                    }

                    Result<string> validated = CampaignPathPolicy.ValidateAndNormalizePath(entry.Path, optionsMonitor.CurrentValue);

                    if (validated.IsFailure
                        || !FileHandleIdentityInterop.TryGetPathIdentity(entry.Path, out FileHandleIdentity currentIdentity)
                        || !FileHandleIdentity.IdentitiesMatch(entry.RootIdentity, currentIdentity))
                    {
                        outcome = WorkspaceUnitOutcome.Failed;
                    }
                    else
                    {
                        EmbeddingSettings embeddings = optionsMonitor.CurrentValue.ResolveEmbeddings();

                        outcome = !embeddings.Enabled || !embeddings.CodebaseRetrievalEnabled
                            ? WorkspaceUnitOutcome.Failed
                            : demand.Full
                                ? await IndexWorkspaceCoreAsync(entry.Path, embeddings, demand, workLease, cancellationToken).ConfigureAwait(false)
                                : await ProcessIncrementalChangesAsync(entry.Path, demand, workLease, embeddings, cancellationToken).ConfigureAwait(false);
                    }

                    lock (_schedulerGate)
                    {
                        if (IsCurrentLocked(entry))
                        {
                            if (outcome == WorkspaceUnitOutcome.Deferred)
                            {
                                entry.Pending.RestoreOlder(demand);
                            }
                            else if (outcome == WorkspaceUnitOutcome.Completed)
                            {
                                if (demand.Full)
                                {
                                    entry.Status.MarkReconciled();
                                }
                                else
                                {
                                    entry.Status.MarkSuccessfulIndex();
                                }
                            }
                            else
                            {
                                entry.Status.MarkDegraded(overflowed: false);

                                if (!demand.Full)
                                {
                                    entry.Pending.RequestForcedReconciliation();
                                }
                            }
                        }
                    }
                }

                if (outcome != WorkspaceUnitOutcome.Deferred)
                {
                    handle.Failed = outcome == WorkspaceUnitOutcome.Failed;

                    return;
                }

                // Scope, whole-file effect and work lease have all settled. This exact handle
                // retains its slot and identity; no dequeue/requeue loop or suffix may overtake it.
                await _workAdmission.WaitForNextOpenGenerationAsync(generation, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown and explicit unregister are terminal, not maintenance deferral.
        }
        catch (Exception ex)
        {
            handle.Failed = true;

            logger.LogWarning(ex, "Workspace indexing failed for {WorkspacePath}.", entry.Path);

            lock (_schedulerGate)
            {
                if (IsCurrentLocked(entry))
                {
                    entry.Status.MarkDegraded(overflowed: false);
                }
            }
        }
        finally
        {
            FinishHandle(handle);
        }
    }

    private void FinishHandle(WorkspaceHandle handle)
    {
        WorkspaceHandle? first = null;

        WorkspaceHandle? second = null;

        handle.DisposeCancellation();

        lock (_schedulerGate)
        {
            WorkspaceEntry entry = handle.Entry;

            if (!_active.TryGetValue(entry.Key, out WorkspaceHandle? current) || !ReferenceEquals(current, handle))
            {
                return;
            }

            _active.Remove(entry.Key);

            entry.Handle = null;

            entry.Status.SetReconciling(false);

            if (handle.Sweep is not null || entry.Retired)
            {
                SettleSweepLocked(entry, handle.Failed);
            }

            handle.Terminal.TrySetResult();

            if (!_intakeClosed)
            {
                if (IsCurrentLocked(entry) && entry.Pending.HasDemand)
                {
                    first = ScheduleLocked(entry);
                }
                else if (_entries.TryGetValue(entry.Key, out WorkspaceEntry? successor)
                    && IsCurrentLocked(successor) && successor.Pending.HasDemand)
                {
                    first = ScheduleLocked(successor);
                }

                first ??= PromoteLocked();

                second = PromoteLocked();
            }
        }

        StartHandle(first);

        StartHandle(second);

        SignalWatcherWork();
    }

    private void EnqueueLocked(WorkspaceEntry entry)
    {
        if (entry.Queued)
        {
            return;
        }

        _overflow ??= new OverflowFifo();

        entry.Queued = true;

        entry.Previous = _overflow.Tail;

        if (_overflow.Tail is not null)
        {
            _overflow.Tail.Next = entry;
        }
        else
        {
            _overflow.Head = entry;
        }

        _overflow.Tail = entry;

        _overflow.Count++;
    }

    private void UnlinkLocked(WorkspaceEntry entry)
    {
        if (!entry.Queued || _overflow is null)
        {
            return;
        }

        if (entry.Previous is null)
        {
            _overflow.Head = entry.Next;
        }
        else
        {
            entry.Previous.Next = entry.Next;
        }

        if (entry.Next is null)
        {
            _overflow.Tail = entry.Previous;
        }
        else
        {
            entry.Next.Previous = entry.Previous;
        }

        entry.Previous = null;

        entry.Next = null;

        entry.Queued = false;

        if (--_overflow.Count == 0)
        {
            _overflow = null;
        }
    }

    private WorkspaceHandle? PromoteLocked()
    {
        if (_active.Count >= WorkspaceCapacity)
        {
            return null;
        }

        WorkspaceEntry? candidate = _overflow?.Head;

        while (candidate is not null)
        {
            WorkspaceEntry? next = candidate.Next;

            if (!_active.ContainsKey(candidate.Key))
            {
                UnlinkLocked(candidate);

                if (IsCurrentLocked(candidate) && candidate.Pending.HasDemand)
                {
                    return PublishHandleLocked(candidate);
                }
            }

            candidate = next;
        }

        return null;
    }

    private bool IsCurrentLocked(WorkspaceEntry entry) =>
        !entry.Retired && _entries.TryGetValue(entry.Key, out WorkspaceEntry? current) && ReferenceEquals(current, entry);

    private static string SchedulerKey(string path) => Path.TrimEndingDirectorySeparator(path);

    private static Error UnavailableError() => new(ErrorCodes.Workspace.IndexingUnavailable, "Workspace indexing is stopping or unavailable.");

    private static Result<WorkspaceIndexQueueDisposition> Unavailable() => Result<WorkspaceIndexQueueDisposition>.Failure(UnavailableError());

    private enum WorkspaceUnitOutcome
    {
        Completed,
        Failed,
        Deferred,
    }

    internal readonly record struct WorkspaceSchedulerSnapshot(int ActiveCount, int QueuedCount, bool OverflowAllocated);

    private sealed class WorkspaceEntry(string key, string path, FileHandleIdentity rootIdentity)
    {
        internal string Key { get; } = key;

        internal string Path { get; } = path;

        internal FileHandleIdentity RootIdentity { get; } = rootIdentity;

        internal RuntimeStatusState Status { get; } = new();

        internal WorkspaceDemand Pending { get; set; } = new();

        internal bool Retired { get; set; }

        internal bool Queued { get; set; }

        internal WorkspaceEntry? Previous { get; set; }

        internal WorkspaceEntry? Next { get; set; }

        internal WorkspaceHandle? Handle { get; set; }

        internal WatcherRegistration? Watcher { get; set; }

        internal ScheduledSweep? Sweep { get; set; }
    }

    private sealed class WorkspaceDemand
    {
        private HashSet<string>? _completedForcedPaths;

        internal Dictionary<string, PendingPathAction> Actions { get; } = new(CanonicalDirectoryComparer);

        internal bool Full { get; set; }

        internal bool ForceAll { get; set; }

        internal int FilesIndexed { get; set; }

        internal bool HasDemand => Full || Actions.Count != 0;

        internal bool ShouldForceFile(string path) =>
            Actions.ContainsKey(path) || (ForceAll && !(_completedForcedPaths?.Contains(path) ?? false));

        internal void MarkFileCompleted(string path)
        {
            Actions.Remove(path);

            if (ForceAll)
            {
                (_completedForcedPaths ??= new HashSet<string>(CanonicalDirectoryComparer)).Add(path);
            }
        }

        internal void RequestForcedReconciliation()
        {
            Full = true;

            ForceAll = true;

            _completedForcedPaths = null;
        }

        internal bool Add(string path, PendingPathAction action)
        {
            if (!Actions.ContainsKey(path) && Actions.Count >= MaxPendingPathsPerWorkspace)
            {
                Actions.Clear();

                RequestForcedReconciliation();

                return true;
            }

            Actions[path] = action;

            return false;
        }

        internal void RestoreOlder(WorkspaceDemand older)
        {
            Full |= older.Full;

            if (!ForceAll && older.ForceAll)
            {
                _completedForcedPaths = older._completedForcedPaths;
            }

            ForceAll |= older.ForceAll;

            FilesIndexed = older.FilesIndexed;

            foreach ((string path, PendingPathAction action) in older.Actions)
            {
                if (!Actions.ContainsKey(path))
                {
                    Add(path, action);
                }
            }
        }
    }

    private sealed class WorkspaceHandle(WorkspaceEntry entry, CancellationToken stoppingToken)
    {
        private readonly object _cancellationGate = new();

        private bool _disposed;

        internal WorkspaceEntry Entry { get; } = entry;

        internal ScheduledSweep? Sweep { get; set; }

        internal bool Failed { get; set; }

        internal CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        internal TaskCompletionSource Terminal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Cancel()
        {
            lock (_cancellationGate)
            {
                if (!_disposed)
                {
                    Cancellation.Cancel();
                }
            }
        }

        internal void DisposeCancellation()
        {
            lock (_cancellationGate)
            {
                _disposed = true;

                Cancellation.Dispose();
            }
        }
    }

    private sealed class OverflowFifo
    {
        internal WorkspaceEntry? Head { get; set; }

        internal WorkspaceEntry? Tail { get; set; }

        internal int Count { get; set; }
    }

    private sealed class ScheduledSweep(int count, int intervalMinutes)
    {
        internal bool Failed { get; set; }

        internal int Outstanding { get; set; } = count;

        internal int IntervalMinutes { get; } = intervalMinutes;
    }
}
