using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Cli.Commands;

/// <summary>
/// Operator view of today's spend against the configured daily budget (requires <c>arcanum serve</c>).
/// </summary>
/// <remarks>
/// Local and delegated spend are reported separately, and delegated work whose peer reported no usage
/// is shown as a count rather than folded in at zero — a spend figure that silently omits part of the
/// day's work is worse than no figure at all (issue #69, docs/Arcanum.DESIGN.md &#167;22.2).
/// </remarks>
public sealed class BudgetCommands(
    ArcanumApiClient apiClient,
    IConsoleDispatcher console,
    ICliInvocationContext invocationContext)
{

    public async Task<int> Show(CancellationToken cancellationToken)
    {

        Result<BudgetSummaryDto> result = await apiClient
            .GetBudgetAsync(cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            console.WriteDiagnostic(result.Error.Message);

            // Exit 3 means "the host was unreachable". A missing key or a server-side domain error
            // is not a network fault and must not tell automation to retry the connection.
            return result.Error.Code.StartsWith("Connection.", StringComparison.Ordinal)
                ? (int)CliExitCode.NetworkError
                : (int)CliExitCode.GenericError;

        }

        BudgetSummaryDto budget = result.Value;

        if (invocationContext.Options.Json)
        {

            console.WriteJson(budget, ArcanumJsonContext.Default.BudgetSummaryDto);

            return (int)CliExitCode.Success;

        }

        if (!budget.Enabled)
        {

            console.WritePayload("Budget: disabled (Arcanum:Cost:Budget:Enabled is off).");

            return (int)CliExitCode.Success;

        }

        console.WritePayload($"Daily limit:   ${budget.DailyLimitUsd:0.00} USD");

        console.WritePayload($"Spent today:   ${budget.TodaySpendUsd:0.00} USD ({budget.SpentPercent}%)");

        console.WritePayload($"  Local:       ${budget.LocalSpendUsd:0.00} USD");

        console.WritePayload($"  Delegated:   ${budget.ExternalSpendUsd:0.00} USD");

        console.WritePayload($"Remaining:     ${budget.RemainingUsd:0.00} USD");

        console.WritePayload($"Alert at:      {budget.AlertThresholdPercent}%");

        if (budget.UnpricedDelegatedSendings > 0)
        {

            console.WritePayload(
                $"Unpriced:      {budget.UnpricedDelegatedSendings} delegated Sending(s) reported no cost, "
                + "so actual spend is higher than the figures above.");

        }

        return (int)CliExitCode.Success;

    }

}
