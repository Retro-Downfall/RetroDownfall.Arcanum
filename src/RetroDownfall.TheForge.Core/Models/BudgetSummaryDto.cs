namespace RetroDownfall.TheForge.Core.Models;

/// <summary>
/// Re-declared mirror of <c>RetroDownfall.Arcanum.Api.Models.BudgetSummaryDto</c> (wire shape of
/// <c>GET /api/budget</c>), used by The Treasury and The Anvil. Kept in TheForge.Core to avoid
/// referencing the Api project.
/// </summary>
public sealed record BudgetSummaryDto(
    bool Enabled,
    decimal DailyLimitUsd,
    int AlertThresholdPercent,
    decimal TodaySpendUsd,
    decimal RemainingUsd,
    int SpentPercent);
