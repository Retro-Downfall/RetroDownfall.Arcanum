using RetroDownfall.Arcanum.Core.Operations;

namespace RetroDownfall.Arcanum.Infrastructure.Operations;

public sealed record LongRunningOperationReconciliationSnapshot(
    bool StartupCompleted,
    bool Deferred,
    DateTimeOffset? LastRunAt,
    LongRunningOperationReconciliationSummary? LastSummary,
    string? PublicDetail);

public sealed class LongRunningOperationReconciliationStatus
{
    private readonly object _gate = new();
    private LongRunningOperationReconciliationSnapshot _snapshot =
        new(false, false, null, null, "Startup reconciliation has not run.");

    public LongRunningOperationReconciliationSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public void Record(
        DateTimeOffset runAt,
        LongRunningOperationReconciliationSummary summary,
        bool startupCompleted = true)
    {
        lock (_gate)
        {
            _snapshot = new(
                startupCompleted,
                Deferred: false,
                runAt,
                summary,
                $"examined={summary.Examined}; completed={summary.Completed}; "
                + $"failed={summary.Failed}; abandoned={summary.Abandoned}; "
                + $"attention={summary.RequiresAttention}; skipped={summary.Skipped}");
        }
    }

    public void RecordDeferred(DateTimeOffset runAt, string publicDetail)
    {
        lock (_gate)
        {
            _snapshot = new(
                StartupCompleted: true,
                Deferred: true,
                runAt,
                LastSummary: null,
                publicDetail);
        }
    }
}
