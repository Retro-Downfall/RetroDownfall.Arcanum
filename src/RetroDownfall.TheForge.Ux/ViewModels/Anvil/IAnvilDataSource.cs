using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Wards;

namespace RetroDownfall.TheForge.Ux.ViewModels.Anvil;

/// <summary>Data-source seam for The Anvil status aggregates.</summary>
public interface IAnvilDataSource
{


    Task<IReadOnlyList<WardDto>> ListWardsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ApprenticeSummaryDto>> ListApprenticesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<McpServerInfo>> ListMcpServersAsync(CancellationToken cancellationToken);

}
