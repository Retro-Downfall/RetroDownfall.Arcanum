namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>
/// Historical reservation recovery and measured usage reporting.
/// </summary>
public interface IBudgetReservationService
{
    /// <summary>Idempotently marks the exact owned active reservation as an unexpiring uncertain paid estimate before dispatch.</summary>
    Task MarkPossiblyDispatchedAsync(Guid reservationId, Guid runId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <summary>Reconciles an abandoned/terminal one-call evaluation run only from its sole durable Completed bill; leaves unknown or live work outstanding.</summary>
    Task ReconcileRecordedUsageAsync(Guid runId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task ReconcileAsync(Guid reservationId, decimal actualCostUsd, CancellationToken cancellationToken = default);

    Task ReleaseAsync(Guid reservationId, CancellationToken cancellationToken = default);

    /// <summary>Sum of completed billable operation costs for the current UTC day.</summary>
    Task<decimal> GetTodayCommittedSpendAsync(CancellationToken cancellationToken = default);

    /// <summary>Sum of current-day Reserved amounts plus unresolved UncertainPaid estimates from every day.</summary>
    Task<decimal> GetTodayOutstandingReservationsAsync(CancellationToken cancellationToken = default);

    Task<int> SweepExpiredAsync(DateTimeOffset utcNow, CancellationToken cancellationToken = default);
}

public sealed record BudgetReservationRequest(
    Guid RunId,
    decimal ReservedUsd,
    DateTimeOffset ExpiresAt,
    string BudgetPeriod);

public sealed record BudgetReservation(
    Guid Id,
    Guid RunId,
    string BudgetPeriod,
    decimal ReservedUsd,
    decimal ReconciledUsd,
    BudgetReservationStatus Status,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt);

public enum BudgetReservationStatus
{
    Reserved = 0,

    Reconciled = 1,

    Released = 2,

    Expired = 3,

    /// <summary>Possibly dispatched: an outstanding estimate, never measured spend. Generic release/expiry cannot erase it.</summary>
    UncertainPaid = 4,
}
