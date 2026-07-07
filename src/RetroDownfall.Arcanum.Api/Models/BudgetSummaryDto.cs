namespace RetroDownfall.Arcanum.Api.Models;

/// <summary>
/// Snapshot of today's cost spend and configured budget, surfaced by <c>GET /api/budget</c>.
/// </summary>
public sealed record BudgetSummaryDto(
    bool Enabled,
    decimal DailyLimitUsd,
    int AlertThresholdPercent,
    decimal TodaySpendUsd,
    decimal RemainingUsd,
    int SpentPercent);
