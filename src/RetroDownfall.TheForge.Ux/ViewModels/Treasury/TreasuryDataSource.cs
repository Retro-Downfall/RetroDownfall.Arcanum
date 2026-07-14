using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Ux.Services.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.Treasury;

/// <summary>API-backed <see cref="ITreasuryDataSource"/> — wraps <see cref="BudgetService"/>.</summary>
public sealed class TreasuryDataSource : ITreasuryDataSource
{

    private readonly BudgetService _budgetService;

    public TreasuryDataSource(BudgetService budgetService)
    {

        _budgetService = budgetService;

    }

    public async Task<BudgetSummaryDto?> GetBudgetAsync(CancellationToken cancellationToken)
    {

        ApiResponse<BudgetSummaryDto>? response = await _budgetService.GetBudgetAsync(cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true, Data: { } budget } ? budget : null;

    }

}
