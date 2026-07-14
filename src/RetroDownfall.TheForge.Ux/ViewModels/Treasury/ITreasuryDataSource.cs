using RetroDownfall.TheForge.Core.Models;

namespace RetroDownfall.TheForge.Ux.ViewModels.Treasury;

/// <summary>
/// Testable seam for The Treasury budget dashboard. Implementations forward to
/// <see cref="RetroDownfall.TheForge.Ux.Services.Services.BudgetService"/> and map
/// <see cref="ApiResponse{T}"/> failures to null without throwing.
/// </summary>
public interface ITreasuryDataSource
{

    Task<BudgetSummaryDto?> GetBudgetAsync(CancellationToken cancellationToken);

}
