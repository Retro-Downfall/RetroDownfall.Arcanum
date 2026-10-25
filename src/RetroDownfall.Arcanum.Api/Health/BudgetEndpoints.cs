using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Api.Health;

internal static class BudgetEndpoints
{
    public static RouteGroupBuilder MapBudgetEndpoints(this RouteGroupBuilder apiGroup)
    {
        apiGroup.MapGet("/budget", async (
            IExternalSpendLedger externalSpend,
            IOptionsSnapshot<ArcanumSettings> settings,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            BudgetSettings budget = settings.Value.ResolveBudget();

            bool enabled = budget.Enabled;

            decimal dailyLimit = ArcanumSettingClamps.BudgetDailyLimitUsd(budget.DailyLimitUsd);

            int alertThreshold = ArcanumSettingClamps.BudgetAlertThresholdPercent(budget.AlertThresholdPercent);

            // Reported through the same authority the gate enforces on — committed BillableOperations for
            // the UTC day plus outstanding reservations — so an operator is never shown a figure that the
            // very next turn is refused on (DESIGN §22.2).
            decimal localSpend = await DailySpendAuthority
                .ResolveLocalSpendAsync(httpContext.RequestServices, cancellationToken)
                .ConfigureAwait(false);

            // Delegated work is real money on someone else's hardware. Reported separately so an operator
            // can tell "we spent it" from "we delegated it", and so the unpriced count is not silently
            // rolled into a total that then looks complete (issue #69).
            ExternalSpendSummary external = await externalSpend.GetTodayAsync(cancellationToken).ConfigureAwait(false);

            decimal todaySpend = localSpend + external.KnownCostUsd;

            bool enforced = enabled && dailyLimit > 0;

            decimal remaining = enforced ? Math.Max(0m, dailyLimit - todaySpend) : 0m;

            int spentPercent = enforced
                ? (int)Math.Min(100, Math.Round(todaySpend * 100m / dailyLimit, MidpointRounding.AwayFromZero))
                : 0;

            BudgetSummaryDto summary = new(
                Enabled: enabled,
                DailyLimitUsd: dailyLimit,
                AlertThresholdPercent: alertThreshold,
                TodaySpendUsd: todaySpend,
                RemainingUsd: remaining,
                SpentPercent: spentPercent,
                LocalSpendUsd: localSpend,
                ExternalSpendUsd: external.KnownCostUsd,
                UnpricedDelegatedSendings: external.UnpricedSendings);

            Result<BudgetSummaryDto> result = Result<BudgetSummaryDto>.Success(summary);

            ApiResponse<BudgetSummaryDto> response = ApiResponse<BudgetSummaryDto>.FromResult(result, traceId);

            return Results.Ok(response);
        })
        .WithName("GetBudget");

        return apiGroup;
    }
}
