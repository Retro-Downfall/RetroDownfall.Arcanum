namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Cost-tracking budget settings. Daily spend is compared against <see cref="DailyLimitUsd"/>; when
/// it crosses <see cref="AlertThresholdPercent"/> (default 80%) a Comm Link warning is dispatched,
/// and at 100% further inference turns are rejected with <c>Budget.Exceeded</c> (HTTP 429).
/// </summary>
public sealed record BudgetSettings
{

    /// <summary>Master toggle. When <see langword="false"/> (default), no budget enforcement occurs.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Maximum USD spend allowed per UTC day before inference is rejected with 429. Default 0
    /// (effectively unlimited when <see cref="Enabled"/> is false). Clamped to &gt;= 0.
    /// </summary>
    public decimal DailyLimitUsd { get; init; }

    /// <summary>
    /// Percentage of <see cref="DailyLimitUsd"/> at which a Comm Link warning is dispatched.
    /// Default 80; clamped 1–100. A unique per-day-per-threshold alert prevents duplicate notifications.
    /// </summary>
    public int AlertThresholdPercent { get; init; } = 80;

}
