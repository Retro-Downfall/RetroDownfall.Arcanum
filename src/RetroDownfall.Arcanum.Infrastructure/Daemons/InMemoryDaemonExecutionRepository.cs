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

        _inFlightByDaemon[daemonId] = executionId;

        return Task.FromResult(executionId);
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

            _ = _inFlightByDaemon.TryRemove(record.DaemonId, out _);

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

            _ = _inFlightByDaemon.TryRemove(record.DaemonId, out _);

            return record.ToSummary();
        }
    }

    private void TrimHistory(string daemonId, List<DaemonExecutionRecord> list)
    {
        int limit = ArcanumSettingClamps.DaemonExecutionHistoryLimit(
            options.CurrentValue.Daemon?.ExecutionHistoryLimit ?? new DaemonSettings().ExecutionHistoryLimit);

        while (list.Count > limit)
        {
            DaemonExecutionRecord removed = list[0];

            list.RemoveAt(0);

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
