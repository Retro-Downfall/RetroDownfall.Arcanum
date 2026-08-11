using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// The one place that answers "what has this instance spent locally today". Daily spend is committed
/// <c>BillableOperations</c> for the UTC day plus outstanding <c>BudgetReservations</c>
/// (docs/Arcanum.DESIGN.md &#167;22.2). The <c>Sessions.TotalCostUsd</c> projection behind
/// <see cref="IGrimoireRepository.GetTodaySpendAsync"/> is keyed on session creation, so it attributes
/// none of today's spend to a session opened yesterday and knows nothing of calls in flight; it is only
/// the fallback for a host with no reservation service (tests / early bootstrap).
/// </summary>
/// <remarks>
/// The budget gate and the budget report both resolve through here so the figure an operator is shown
/// can never drift from the figure they are refused on.
/// </remarks>
internal static class DailySpendAuthority
{

    public static async Task<decimal> ResolveLocalSpendAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {

        IBudgetReservationService? reservations = services.GetService<IBudgetReservationService>();

        if (reservations is null)
        {

            IGrimoireRepository grimoire = services.GetRequiredService<IGrimoireRepository>();

            return await grimoire.GetTodaySpendAsync(cancellationToken).ConfigureAwait(false);

        }

        decimal committed = await reservations.GetTodayCommittedSpendAsync(cancellationToken).ConfigureAwait(false);

        decimal outstanding = await reservations.GetTodayOutstandingReservationsAsync(cancellationToken)
            .ConfigureAwait(false);

        return committed + outstanding;

    }

}
