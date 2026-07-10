using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Wards;
using RetroDownfall.TheForge.Core.Models;

namespace RetroDownfall.TheForge.Ux.ViewModels.Anvil;

/// <summary>Data-source seam for The Anvil status aggregates.</summary>
public interface IAnvilDataSource
{

    Task<BudgetSummaryDto?> GetBudgetAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<WardDto>> ListWardsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ApprenticeSummaryDto>> ListApprenticesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<McpServerInfo>> ListMcpServersAsync(CancellationToken cancellationToken);

}
