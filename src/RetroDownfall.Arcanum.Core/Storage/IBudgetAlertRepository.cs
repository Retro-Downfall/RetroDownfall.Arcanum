using RetroDownfall.Arcanum.Core.Storage.Entities;

namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>
/// Persistence surface for <see cref="BudgetAlert"/> rows. The unique index on (Threshold, date(AlertedAt))
/// prevents duplicate alerts; <see cref="RecordAlertAsync"/> gracefully handles constraint violations.
/// </summary>
public interface IBudgetAlertRepository
{

    /// <summary>
    /// Records a budget alert. Returns <see langword="true"/> when a new row was inserted, or
    /// <see langword="false"/> when a duplicate alert for the same (threshold, UTC day) already exists
    /// (the constraint violation is swallowed). Never throws on duplicate inserts.
    /// </summary>
    Task<bool> RecordAlertAsync(
        int threshold,
        decimal spendUsd,
        decimal dailyLimitUsd,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns true when an alert for the supplied <paramref name="threshold"/> has already been
    /// recorded today (UTC). Used as a cheap pre-check to avoid the insert path entirely.
    /// </summary>
    Task<bool> HasAlertedTodayAsync(int threshold, CancellationToken cancellationToken = default);

}
