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
    /// committed spend + outstanding reservations + this estimate would exceed the daily limit. A negative
    /// <see cref="BudgetReservationRequest.ReservedUsd"/> is a caller defect and throws
    /// <see cref="ArgumentOutOfRangeException"/> instead of lowering the outstanding sum.
    /// </summary>
    Task<Result<BudgetReservation>> ReserveAsync(
        BudgetReservationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Raises an existing reservation after the materialized context has been estimated. The
    /// implementation must apply the same atomic daily-limit check as initial acquisition.
    /// </summary>
    Task<Result> AdjustAsync(
        Guid reservationId,
        decimal reservedUsd,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rechecks the daily limit for a still-<see cref="BudgetReservationStatus.Reserved"/> reservation
    /// without raising it: committed spend plus outstanding reservations (this one included) in the
    /// reservation's own budget period, plus <paramref name="delegatedSpendUsd"/>, must not exceed the
    /// limit. Fails with <c>Budget.Exceeded</c> when it would.
    /// </summary>
    /// <remarks>
    /// The same ledger and the same limit source <see cref="AdjustAsync"/> judges a raise on, so a
    /// round that raises and a round that does not are held to one figure; a cheap read rather than a
    /// write transaction. A reservation that is missing or no longer <c>Reserved</c> has nothing
    /// outstanding to check and succeeds, as a raise of it would.
    /// </remarks>
    Task<Result> RecheckDailyLimitAsync(
        Guid reservationId,
        decimal delegatedSpendUsd,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a still-<see cref="BudgetReservationStatus.Reserved"/> reservation's expiry forward to
    /// <paramref name="expiresAt"/>. Never shortens it, and never touches a settled reservation.
    /// </summary>
    /// <remarks>
    /// Renewed before every provider call that ledgers against the reservation, an owning turn's and a
    /// batch line's alike, so work that runs past the lifetime it was admitted with still reconciles a
    /// reservation that is <c>Reserved</c> rather than expired. Callers treat a failed renewal as
    /// bookkeeping lost, not as a refused call.
    /// </remarks>
    Task ExtendExpiryAsync(Guid reservationId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);

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
