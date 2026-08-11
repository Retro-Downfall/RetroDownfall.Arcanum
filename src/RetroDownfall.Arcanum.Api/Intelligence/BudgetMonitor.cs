using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.CommLink;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Stateless singleton that compares today's spend — resolved through
/// <see cref="DailySpendAuthority"/>, the same resolver <c>GET /api/budget</c> reports from — against
/// <see cref="BudgetSettings"/>. At 100% of the daily limit, <see cref="CheckAsync"/> returns
/// <see cref="ErrorCodes.Budget.Exceeded"/> before provider calls.
/// </summary>
public sealed class BudgetMonitor(
    IServiceScopeFactory scopeFactory,
    ICommLinkDispatcher commLink,
    IOptionsMonitor<ArcanumSettings> settings,
    ILogger<BudgetMonitor> logger)
{

    /// <summary>
    /// Checks today's spend against the configured budget. Returns a failure result with
    /// <see cref="ErrorCodes.Budget.Exceeded"/> when the daily limit has been reached; otherwise
    /// dispatches an alert (once per threshold per UTC day) when the alert threshold is crossed.
    /// </summary>
    public async Task<Result> CheckAsync(CancellationToken cancellationToken = default)
    {

        BudgetSettings budget = settings.CurrentValue.ResolveBudget();

        if (!budget.Enabled || budget.DailyLimitUsd <= 0)
        {

            return Result.Success();

        }

        decimal dailyLimit = ArcanumSettingClamps.BudgetDailyLimitUsd(budget.DailyLimitUsd);

        int alertThreshold = ArcanumSettingClamps.BudgetAlertThresholdPercent(budget.AlertThresholdPercent);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        IBudgetAlertRepository budgetAlerts = scope.ServiceProvider.GetRequiredService<IBudgetAlertRepository>();

        decimal spend = await DailySpendAuthority
            .ResolveLocalSpendAsync(scope.ServiceProvider, cancellationToken)
            .ConfigureAwait(false);

        ExternalSpendSummary external = await ResolveExternalSpendAsync(scope.ServiceProvider, cancellationToken)
            .ConfigureAwait(false);

        // Delegated work counts against the ceiling: an operator who can blow through a spend cap simply
        // by dispatching Sendings does not have a spend cap (issue #69).
        spend += external.KnownCostUsd;

        if (spend >= dailyLimit)
        {

            await TryDispatchAlertAsync(
                budgetAlerts,
                threshold: 100,
                spend,
                dailyLimit,
                CommLinkSeverity.Critical,
                cancellationToken).ConfigureAwait(false);

            return Result.Failure(new Error(
                ErrorCodes.Budget.Exceeded,
                $"Daily budget limit of ${dailyLimit:0.00} USD has been reached (current spend: ${spend:0.00} USD)."
                + DescribeUnpricedDelegation(external)));

        }

        decimal alertTriggerSpend = dailyLimit * alertThreshold / 100m;

        if (spend >= alertTriggerSpend)
        {

            await TryDispatchAlertAsync(
                budgetAlerts,
                threshold: alertThreshold,
                spend,
                dailyLimit,
                CommLinkSeverity.Warning,
                cancellationToken).ConfigureAwait(false);

        }

        return Result.Success();

    }

    /// <summary>
    /// Today's delegated spend, or nothing when no ledger is registered (a Grimoire-less host).
    /// </summary>
    private static async Task<ExternalSpendSummary> ResolveExternalSpendAsync(
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        services.GetService<IExternalSpendLedger>() is { } ledger
            ? await ledger.GetTodayAsync(cancellationToken).ConfigureAwait(false)
            : ExternalSpendSummary.None;

    /// <summary>
    /// Names unpriced delegated work on a ceiling refusal.
    /// </summary>
    /// <remarks>
    /// A peer that reported nothing is allowed through and flagged, never charged as zero and never
    /// silently allowed: the operator is told that the figure they just hit their ceiling on is a floor
    /// (issue #69, docs/Arcanum.DESIGN.md &#167;22.2).
    /// </remarks>
    private static string DescribeUnpricedDelegation(ExternalSpendSummary external) =>
        external.HasUnpricedDelegation
            ? $" {external.UnpricedSendings} delegated Sending(s) today reported no cost at all, so actual "
                + "spend is higher than this figure."
            : string.Empty;

    private async Task TryDispatchAlertAsync(
        IBudgetAlertRepository budgetAlerts,
        int threshold,
        decimal spend,
        decimal dailyLimit,
        CommLinkSeverity severity,
        CancellationToken cancellationToken)
    {

        try
        {

            bool recorded = await budgetAlerts.RecordAlertAsync(threshold, spend, dailyLimit, cancellationToken).ConfigureAwait(false);

            if (!recorded)
            {

                logger.LogDebug(
                    "Budget alert for threshold {Threshold} was already recorded today by another concurrent turn; skipping duplicate notification.",
                    threshold);

                return;

            }

            string title = threshold >= 100
                ? "Arcanum daily budget exhausted"
                : $"Arcanum daily budget at {threshold}%";

            string body = $"Today's spend is ${spend:0.00} USD of the ${dailyLimit:0.00} USD daily limit ({threshold}%).";

            CommLinkMessage message = new(title, body, severity, "budget");

            await commLink.DispatchAsync(message, cancellationToken).ConfigureAwait(false);

        }
        catch (OperationCanceledException)
        {

            throw;

        }
        catch (Exception ex)
        {

            logger.LogWarning(ex, "Failed to dispatch budget alert for threshold {Threshold}.", threshold);

        }

    }

}
