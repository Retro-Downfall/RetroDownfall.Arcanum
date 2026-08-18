using System.Collections.Concurrent;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Daemons;
using RetroDownfall.Arcanum.Core.Logging;
using RetroDownfall.Arcanum.Infrastructure.Logging;

namespace RetroDownfall.Arcanum.Infrastructure.Daemons;

internal sealed class DaemonExecutionRecord
{

    public required string Id { get; init; }

    public required string DaemonId { get; init; }

    public required string DaemonName { get; init; }

    public DaemonJobStatus Status { get; set; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string? ErrorMessage { get; set; }

    public CancellationTokenSource? Cancellation { get; set; }

    public DaemonExecutionSummary ToSummary() =>
        new(Id, DaemonId, DaemonName, Status, StartedAt, CompletedAt, ErrorMessage);

    public DaemonExecutionDetail ToDetail(LogEntry[] logs) =>
        new(Id, DaemonId, DaemonName, Status, StartedAt, CompletedAt, ErrorMessage, logs);

}

public sealed class InMemoryDaemonExecutionRepository(
    ILogRingBuffer logRingBuffer,
    TimeProvider? timeProvider = null) :
    IDaemonExecutionRepository,
    IDaemonExecutionMutationGate
{

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    private readonly ConcurrentDictionary<string, List<DaemonExecutionRecord>> _history = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, DaemonExecutionRecord> _byId = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, string> _inFlightByDaemon = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    public Task<DaemonExecutionSummary[]> GetHistoryAsync(string? daemonId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(daemonId))
        {
            DaemonExecutionSummary[] all = _byId.Values
                .OrderByDescending(r => r.StartedAt)
                .Select(r => r.ToSummary())
                .ToArray();

            return Task.FromResult(all);
        }

        if (!_history.TryGetValue(daemonId, out List<DaemonExecutionRecord>? list))
        {
            return Task.FromResult(Array.Empty<DaemonExecutionSummary>());
        }

        lock (GetLock(daemonId))
        {
            DaemonExecutionSummary[] result = list
                .OrderByDescending(r => r.StartedAt)
                .Select(r => r.ToSummary())
                .ToArray();

            return Task.FromResult(result);
        }
    }

    public Task<DaemonExecutionDetail?> GetAsync(string executionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_byId.TryGetValue(executionId, out DaemonExecutionRecord? record))
        {
            return Task.FromResult<DaemonExecutionDetail?>(null);
        }

        lock (GetLock(record.DaemonId))
        {
            LogEntry[] logs = logRingBuffer
                .GetSnapshot()
                .Where(e => string.Equals(e.CorrelationId, executionId, StringComparison.Ordinal))
                .OrderBy(e => e.Sequence)
                .ToArray();

            return Task.FromResult<DaemonExecutionDetail?>(record.ToDetail(logs));
        }
    }

    public async Task<string> StartAsync(
        string daemonId,
        string daemonName,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        await using IAsyncDisposable lease = await AcquireExclusiveAsync(
            ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();

        string executionId = Guid.NewGuid().ToString("N");

        // Single-flight: refuse to overwrite an existing in-flight slot for this daemon.
        if (!_inFlightByDaemon.TryAdd(daemonId, executionId))
        {

            throw new InvalidOperationException(
                $"Daemon job '{daemonId}' already has a running execution.");

        }

        try
        {

            CreateRecord(daemonId, daemonName, executionId, ct);

        }
        catch
        {

            _ = _inFlightByDaemon.TryRemove(daemonId, out _);

            throw;

        }

        return executionId;
    }

    // W3.3 Fix 4: atomic single-running reservation for the on-demand path. The
    // TryAdd on _inFlightByDaemon is the single atomic step that combines the
    // "not already running" check with the slot reservation; a concurrent call
    // for the same daemon loses the TryAdd and returns false without creating a
    // record. The caller supplies the executionId so DaemonRunner can use it for
    // the subsequent GetCancellationTokenSource lookup without a second round-trip.
    public async Task<bool> TryStartAsync(
        string daemonId,
        string daemonName,
        string executionId,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        await using IAsyncDisposable lease = await AcquireExclusiveAsync(
            ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();

        if (!_inFlightByDaemon.TryAdd(daemonId, executionId))
        {

            return false;

        }

        try
        {

            CreateRecord(daemonId, daemonName, executionId, ct);

        }
        catch
        {

            _ = _inFlightByDaemon.TryRemove(daemonId, out _);

            throw;

        }

        return true;
    }

    public async ValueTask<IAsyncDisposable> AcquireExclusiveAsync(
        CancellationToken cancellationToken = default)
    {

        await _mutationGate
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        return new MutationLease(_mutationGate);

    }

    private DaemonExecutionRecord CreateRecord(string daemonId, string daemonName, string executionId, CancellationToken ct)
    {
        DaemonExecutionRecord record = new()
        {
            Id = executionId,
            DaemonId = daemonId,
            DaemonName = daemonName,
            Status = DaemonJobStatus.Running,
            StartedAt = _timeProvider.GetUtcNow(),
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct),
        };

        lock (GetLock(daemonId))
        {
            List<DaemonExecutionRecord> list = _history.GetOrAdd(daemonId, _ => []);

            list.Add(record);

            TrimHistory(daemonId, list);
        }

        _byId[executionId] = record;

        return record;
    }

    public Task<DaemonExecutionSummary> CompleteAsync(string executionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(UpdateStatus(executionId, DaemonJobStatus.Completed, null));
    }

    public Task<DaemonExecutionSummary> FailAsync(string executionId, string errorMessage, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(UpdateStatus(executionId, DaemonJobStatus.Failed, errorMessage));
    }

    public Task<DaemonExecutionSummary> CancelAsync(string executionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_byId.TryGetValue(executionId, out DaemonExecutionRecord? record))
        {
            throw new InvalidOperationException($"Execution '{executionId}' was not found.");
        }

        lock (GetLock(record.DaemonId))
        {
            if (record.Status == DaemonJobStatus.Cancelled)
            {
                // Re-entry is DaemonRunner reporting that job.RunAsync has actually returned, so this is
                // where the reservation is finally released and the token source disposed.
                ReleaseDrainedExecution(record, executionId);

                return Task.FromResult(record.ToSummary());
            }

            if (record.Status != DaemonJobStatus.Running)
            {
                throw new InvalidOperationException($"Execution '{executionId}' is not running.");
            }

            try
            {
                record.Cancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            record.Status = DaemonJobStatus.Cancelled;

            record.CompletedAt = _timeProvider.GetUtcNow();

            record.ErrorMessage = "Job was cancelled.";

            // Cancel, but neither dispose the token source nor release the in-flight reservation:
            // cancellation is cooperative, so the job body is still executing and still holds that token.
            // The reservation is this daemon's only single-flight mechanism, so freeing it here would let a
            // second headless run start against the same target spell while the first is mid-turn. Both
            // happen in ReleaseDrainedExecution once a terminal transition reports the body has returned —
            // the cancel-not-dispose ordering the Apprentice runtime already uses (DESIGN §5.7).
            return Task.FromResult(record.ToSummary());
        }
    }

    public bool HasRunningExecution(string daemonId) =>
        _inFlightByDaemon.ContainsKey(daemonId);

    public Task<bool> TryDeleteTerminalAsync(
        string executionId,
        CancellationToken ct)
        => TryDeleteTerminalBeforeAsync(
            executionId,
            DateTimeOffset.MaxValue,
            ct);

    public Task<bool> TryDeleteTerminalBeforeAsync(
        string executionId,
        DateTimeOffset completedAtCutoff,
        CancellationToken ct)
    {

        ct.ThrowIfCancellationRequested();

        if (!_byId.TryGetValue(executionId, out DaemonExecutionRecord? record))
        {

            return Task.FromResult(false);

        }

        lock (GetLock(record.DaemonId))
        {

            if (!_byId.TryGetValue(executionId, out DaemonExecutionRecord? current)
                || !ReferenceEquals(record, current)
                || current.Status is not (
                    DaemonJobStatus.Completed
                    or DaemonJobStatus.Failed
                    or DaemonJobStatus.Cancelled)
                || current.CompletedAt is not DateTimeOffset completedAt
                || completedAt > completedAtCutoff)
            {

                return Task.FromResult(false);

            }

            if (!_history.TryGetValue(
                    current.DaemonId,
                    out List<DaemonExecutionRecord>? history)
                || !history.Remove(current))
            {

                return Task.FromResult(false);

            }

            _ = _byId.TryRemove(executionId, out _);

            return Task.FromResult(true);

        }

    }

    public CancellationTokenSource? GetCancellationTokenSource(string executionId)
    {
        if (!_byId.TryGetValue(executionId, out DaemonExecutionRecord? record))
        {
            return null;
        }

        lock (GetLock(record.DaemonId))
        {
            return record.Cancellation;
        }
    }

    private DaemonExecutionSummary UpdateStatus(string executionId, DaemonJobStatus status, string? errorMessage)
    {
        if (!_byId.TryGetValue(executionId, out DaemonExecutionRecord? record))
        {
            throw new InvalidOperationException($"Execution '{executionId}' was not found.");
        }

        lock (GetLock(record.DaemonId))
        {
            // Terminal CAS: only Running → terminal; first terminal wins; later Complete/Fail no-op on the
            // recorded status — but a job cancelled out of band drains through exactly this call, so the
            // release still has to happen here or the daemon stays reserved for the life of the host.
            if (record.Status != DaemonJobStatus.Running)
            {
                ReleaseDrainedExecution(record, executionId);

                return record.ToSummary();
            }

            record.Status = status;

            record.CompletedAt = _timeProvider.GetUtcNow();

            record.ErrorMessage = errorMessage;

            ReleaseDrainedExecution(record, executionId);

            return record.ToSummary();
        }
    }

    /// <summary>
    /// The one place an execution's runtime resources are handed back: called only from a terminal
    /// transition, which the runner performs after <c>job.RunAsync</c> has returned. Idempotent and
    /// id-matched, so the repeated terminal calls a cancelled-then-completed execution produces are safe.
    /// </summary>
    private void ReleaseDrainedExecution(DaemonExecutionRecord record, string executionId)
    {

        DisposeCancellation(record);

        RemoveInFlightIfMatch(record.DaemonId, executionId);

    }

    // W3.3 Fix 4: id-matched removal. Only evict the in-flight slot when the
    // stored execution id equals the one being completed/cancelled. StartAsync
    // (scheduled path) overwrites the slot blindly, so a later execution may hold
    // it; a late completion of the earlier execution must not evict the later one.
    // TryAdd (TryStart) fails while the key is present, so the compare-then-remove
    // window is safe: no other TryStart can claim the slot between the read and the
    // remove, and StartAsync overwrites are handled because the comparison guards.
    private void RemoveInFlightIfMatch(string daemonId, string executionId)
    {

        if (_inFlightByDaemon.TryGetValue(daemonId, out string? currentId)
            && string.Equals(currentId, executionId, StringComparison.Ordinal))
        {

            _ = _inFlightByDaemon.TryRemove(daemonId, out _);

        }

    }

    private void TrimHistory(string daemonId, List<DaemonExecutionRecord> list)
    {
        int limit = ArcanumSettingClamps.DaemonExecutionHistoryLimit(
            ArcanumRuntimeDefaults.DaemonExecutionHistoryLimit);

        while (list.Count > limit)
        {
            // Never evict a live Running execution — doing so leaves the in-flight slot
            // pointing at a missing record and can permanently block future starts.
            int removeAt = -1;

            for (int i = 0; i < list.Count; i++)
            {

                if (list[i].Status != DaemonJobStatus.Running)
                {

                    removeAt = i;

                    break;

                }

            }

            if (removeAt < 0)
            {

                break;

            }

            DaemonExecutionRecord removed = list[removeAt];

            list.RemoveAt(removeAt);

            _ = _byId.TryRemove(removed.Id, out _);
        }
    }

    private object GetLock(string daemonId) => _locks.GetOrAdd(daemonId, _ => new object());

    private static void DisposeCancellation(DaemonExecutionRecord record)
    {
        CancellationTokenSource? cts = record.Cancellation;

        record.Cancellation = null;

        cts?.Dispose();
    }

    private sealed class MutationLease(SemaphoreSlim gate) : IAsyncDisposable
    {

        private int _released;

        public ValueTask DisposeAsync()
        {

            if (Interlocked.Exchange(ref _released, 1) == 0)
            {

                _ = gate.Release();

            }

            return ValueTask.CompletedTask;

        }

    }

}
