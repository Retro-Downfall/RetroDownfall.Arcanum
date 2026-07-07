namespace RetroDownfall.Arcanum.Core.Storage.Entities;

/// <summary>
/// One row per (threshold, UTC day) budget alert dispatch. The unique index
/// <c>IX_BudgetAlerts_Threshold_Date</c> on (<c>Threshold</c>, <c>date(AlertedAt)</c>) prevents duplicate
/// alerts for the same threshold on the same day at the database level.
/// </summary>
public sealed class BudgetAlert
{

    public Guid Id { get; set; }

    /// <summary>Percentage threshold that was crossed (e.g. 80, 100).</summary>
    public int Threshold { get; set; }

    public DateTimeOffset AlertedAt { get; set; }

    /// <summary>USD spend observed when the threshold was crossed.</summary>
    public decimal SpendUsd { get; set; }

    /// <summary>Daily limit in effect at the time of the alert.</summary>
    public decimal DailyLimitUsd { get; set; }

}
