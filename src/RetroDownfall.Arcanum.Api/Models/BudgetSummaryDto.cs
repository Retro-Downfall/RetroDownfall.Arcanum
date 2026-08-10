namespace RetroDownfall.Arcanum.Api.Models;

/// <summary>
/// Snapshot of today's cost spend and configured budget, surfaced by <c>GET /api/budget</c>.
/// </summary>
/// <param name="TodaySpendUsd">
/// <paramref name="LocalSpendUsd"/> plus <paramref name="ExternalSpendUsd"/> — everything with a price.
/// </param>
/// <param name="LocalSpendUsd">What this instance's own inference cost today.</param>
/// <param name="ExternalSpendUsd">
/// What delegated (A2A) work cost today, as reported by the peers that ran it (issue #69).
/// </param>
/// <param name="UnpricedDelegatedSendings">
/// Sendings that settled today whose peer reported no usage at all. They are <em>counted</em>, never
/// costed: adding them at zero would make the total look complete when part of the day's delegated work
/// has no price. A non-zero count means the figures above are a floor, not the whole bill.
/// </param>
public sealed record BudgetSummaryDto(
    bool Enabled,
    decimal DailyLimitUsd,
    int AlertThresholdPercent,
    decimal TodaySpendUsd,
    decimal RemainingUsd,
    int SpentPercent,
    decimal LocalSpendUsd = 0m,
    decimal ExternalSpendUsd = 0m,
    int UnpricedDelegatedSendings = 0);
