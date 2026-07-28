using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
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
            IGrimoireRepository grimoire,
            IOptionsSnapshot<ArcanumSettings> settings,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {

            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            BudgetSettings budget = settings.Value.Budget;

            bool enabled = budget.Enabled;

            decimal dailyLimit = ArcanumSettingClamps.BudgetDailyLimitUsd(budget.DailyLimitUsd);

            int alertThreshold = ArcanumSettingClamps.BudgetAlertThresholdPercent(budget.AlertThresholdPercent);

            decimal todaySpend = enabled && dailyLimit > 0
                ? await grimoire.GetTodaySpendAsync(cancellationToken).ConfigureAwait(false)
                : 0m;

            decimal remaining = Math.Max(0m, dailyLimit - todaySpend);

            int spentPercent = dailyLimit > 0
                ? (int)Math.Min(100, Math.Round(todaySpend * 100m / dailyLimit, MidpointRounding.AwayFromZero))
                : 0;

            BudgetSummaryDto summary = new(
                Enabled: enabled,
                DailyLimitUsd: dailyLimit,
                AlertThresholdPercent: alertThreshold,
                TodaySpendUsd: todaySpend,
                RemainingUsd: remaining,
                SpentPercent: spentPercent);

            Result<BudgetSummaryDto> result = Result<BudgetSummaryDto>.Success(summary);

            ApiResponse<BudgetSummaryDto> response = ApiResponse<BudgetSummaryDto>.FromResult(result, traceId);

            return Results.Ok(response);

        })
        .WithName("GetBudget");

        return apiGroup;

    }

}
