namespace RetroDownfall.TheForge.Core.Models;

/// <summary>
/// Re-declared mirror of <c>RetroDownfall.Arcanum.Api.Models.BudgetSummaryDto</c> (wire shape of
/// <c>GET /api/budget</c>), used by The Treasury and The Anvil. Kept in TheForge.Core to avoid
/// referencing the Api project.
/// </summary>
/// <param name="LocalSpendUsd">What this instance's own inference cost today.</param>
/// <param name="ExternalSpendUsd">What delegated (A2A) work cost today, as reported by peers.</param>
/// <param name="UnpricedDelegatedSendings">
/// Delegated Sendings that settled today whose peer reported nothing. Counted, never costed — a
/// non-zero value means the spend figures are a floor rather than the whole bill (issue #69).
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
