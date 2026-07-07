using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.CommLink;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Stateless singleton that compares today's accumulated USD spend (read from <see cref="IGrimoireRepository.GetTodaySpendAsync"/>)
/// against <see cref="BudgetSettings"/>. When spend crosses <see cref="BudgetSettings.AlertThresholdPercent"/>
/// and no alert for that threshold has been recorded today, a Comm Link warning is dispatched and a
/// <see cref="IBudgetAlertRepository"/> row is inserted to prevent duplicates. At 100% of the daily limit,
/// <see cref="CheckAsync"/> returns a <see cref="ErrorCodes.Budget.Exceeded"/> failure so the caller can
/// reject the inference turn with HTTP 429 before any provider call is made.
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

        BudgetSettings budget = settings.CurrentValue.Budget;

        if (!budget.Enabled || budget.DailyLimitUsd <= 0)
        {

            return Result.Success();

        }

        decimal dailyLimit = ArcanumSettingClamps.BudgetDailyLimitUsd(budget.DailyLimitUsd);

        int alertThreshold = ArcanumSettingClamps.BudgetAlertThresholdPercent(budget.AlertThresholdPercent);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        IGrimoireRepository grimoire = scope.ServiceProvider.GetRequiredService<IGrimoireRepository>();

        IBudgetAlertRepository budgetAlerts = scope.ServiceProvider.GetRequiredService<IBudgetAlertRepository>();

        decimal spend = await grimoire.GetTodaySpendAsync(cancellationToken).ConfigureAwait(false);

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
                $"Daily budget limit of ${dailyLimit:0.00} USD has been reached (current spend: ${spend:0.00} USD)."));

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
