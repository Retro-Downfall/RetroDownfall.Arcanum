using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Conclave;

namespace RetroDownfall.Arcanum.Tests.Operations;

/// <summary>
/// Records inference-run transitions with the same compare-and-set rule the SQL writer applies, so a
/// second recovery pass cannot silently downgrade a run that already completed.
/// </summary>
internal sealed class FakeTurnRunWriter : ITurnRunWriter
{
    private readonly Dictionary<Guid, InferenceRunStatus> _runs = [];

    public List<Guid> AbandonAttempts { get; } = [];

    public List<BillableOperationRecord> Billed { get; } = [];

    public void SeedRun(Guid runId, InferenceRunStatus status) => _runs[runId] = status;

    public InferenceRunStatus? StatusOf(Guid runId) =>
        _runs.TryGetValue(runId, out InferenceRunStatus status) ? status : null;

    public Task<Guid> StartRunAsync(InferenceRunStart start, CancellationToken cancellationToken = default)
    {
        Guid id = Guid.NewGuid();

        _runs[id] = InferenceRunStatus.Running;

        return Task.FromResult(id);
    }

    public Task CompleteRunAsync(Guid runId, InferenceRunStatus status, CancellationToken cancellationToken = default)
    {
        _runs[runId] = status;

        return Task.CompletedTask;
    }

    public Task<bool> TryAbandonRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        AbandonAttempts.Add(runId);

        if (!_runs.TryGetValue(runId, out InferenceRunStatus status)
            || status != InferenceRunStatus.Running)
        {
            return Task.FromResult(false);
        }

        _runs[runId] = InferenceRunStatus.Abandoned;

        return Task.FromResult(true);
    }

    public Task<Guid> RecordBillableOperationAsync(
        BillableOperationRecord operation,
        CancellationToken cancellationToken = default)
    {
        Billed.Add(operation);

        return Task.FromResult(Guid.NewGuid());
    }
}

internal sealed class FakeBudgetReservationService : IBudgetReservationService
{
    public List<Guid> Released { get; } = [];

    public Task<Result<BudgetReservation>> ReserveAsync(
        BudgetReservationRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Result> AdjustAsync(
        Guid reservationId,
        decimal reservedUsd,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task ReconcileAsync(Guid reservationId, decimal actualCostUsd, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task ReleaseAsync(Guid reservationId, CancellationToken cancellationToken = default)
    {
        Released.Add(reservationId);

        return Task.CompletedTask;
    }

    public Task<decimal> GetTodayCommittedSpendAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(0m);

    public Task<decimal> GetTodayOutstandingReservationsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(0m);

    public Task ExtendExpiryAsync(Guid reservationId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<int> SweepExpiredAsync(DateTimeOffset utcNow, CancellationToken cancellationToken = default) =>
        Task.FromResult(0);
}
