using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>
/// Atomic daily budget reservations. Acquisition uses a tiny <c>BEGIN IMMEDIATE</c> transaction —
/// never held across inference.
/// </summary>
public interface IBudgetReservationService
{

    /// <summary>
    /// Estimates and reserves cost for a turn. Fails with <c>Budget.Exceeded</c> when today's
    /// committed spend + outstanding reservations + this estimate would exceed the daily limit.
    /// </summary>
    Task<Result<BudgetReservation>> ReserveAsync(
        BudgetReservationRequest request,
        CancellationToken cancellationToken = default);

    Task ReconcileAsync(Guid reservationId, decimal actualCostUsd, CancellationToken cancellationToken = default);

    Task ReleaseAsync(Guid reservationId, CancellationToken cancellationToken = default);

    /// <summary>Sum of completed billable operation costs for the current UTC day.</summary>
    Task<decimal> GetTodayCommittedSpendAsync(CancellationToken cancellationToken = default);

    /// <summary>Sum of outstanding (Reserved, not reconciled) reservation amounts for the current UTC day.</summary>
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

}
