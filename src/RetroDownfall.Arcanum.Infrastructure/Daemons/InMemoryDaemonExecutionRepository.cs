using System.Collections.Concurrent;

using Microsoft.Extensions.Options;

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
    IOptionsMonitor<ArcanumSettings> options,
    ILogRingBuffer logRingBuffer) : IDaemonExecutionRepository
{

    private readonly ConcurrentDictionary<string, List<DaemonExecutionRecord>> _history = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, DaemonExecutionRecord> _byId = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, string> _inFlightByDaemon = new(StringComparer.Ordinal);

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

    public Task<string> StartAsync(string daemonId, string daemonName, CancellationToken ct)
    {
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

        return Task.FromResult(executionId);
    }

    // W3.3 Fix 4: atomic single-running reservation for the on-demand path. The
    // TryAdd on _inFlightByDaemon is the single atomic step that combines the
    // "not already running" check with the slot reservation; a concurrent call
    // for the same daemon loses the TryAdd and returns false without creating a
    // record. The caller supplies the executionId so DaemonRunner can use it for
    // the subsequent GetCancellationTokenSource lookup without a second round-trip.
    public Task<bool> TryStartAsync(string daemonId, string daemonName, string executionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_inFlightByDaemon.TryAdd(daemonId, executionId))
        {

            return Task.FromResult(false);

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

        return Task.FromResult(true);
    }

    private DaemonExecutionRecord CreateRecord(string daemonId, string daemonName, string executionId, CancellationToken ct)
    {
        DaemonExecutionRecord record = new()
        {
            Id = executionId,
            DaemonId = daemonId,
            DaemonName = daemonName,
            Status = DaemonJobStatus.Running,
            StartedAt = DateTimeOffset.UtcNow,
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

            record.CompletedAt = DateTimeOffset.UtcNow;

            record.ErrorMessage = "Job was cancelled.";

            DisposeCancellation(record);

            RemoveInFlightIfMatch(record.DaemonId, executionId);

            return Task.FromResult(record.ToSummary());
        }
    }

    public bool HasRunningExecution(string daemonId) =>
        _inFlightByDaemon.ContainsKey(daemonId);

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
            record.Status = status;

            record.CompletedAt = DateTimeOffset.UtcNow;

            record.ErrorMessage = errorMessage;

            DisposeCancellation(record);

            RemoveInFlightIfMatch(record.DaemonId, executionId);

            return record.ToSummary();
        }
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
            options.CurrentValue.Daemon?.ExecutionHistoryLimit ?? new DaemonSettings().ExecutionHistoryLimit);

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

}
