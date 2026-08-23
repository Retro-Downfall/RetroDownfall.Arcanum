using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Wards;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Ux.Services.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.Anvil;

/// <summary>API-backed Anvil data source.</summary>
public sealed class AnvilDataSource : IAnvilDataSource
{

    private readonly BudgetService _budgetService;

    private readonly WardService _wardService;

    private readonly ApprenticeService _apprenticeService;

    private readonly McpService _mcpService;

    public AnvilDataSource(
        BudgetService budgetService,
        WardService wardService,
        ApprenticeService apprenticeService,
        McpService mcpService)
    {

        _budgetService = budgetService;

        _wardService = wardService;

        _apprenticeService = apprenticeService;

        _mcpService = mcpService;

    }

    public async Task<BudgetSummaryDto?> GetBudgetAsync(CancellationToken cancellationToken)
    {

        ApiResponse<BudgetSummaryDto>? response = await _budgetService.GetBudgetAsync(cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true } ? response.Data : null;

    }

    public async Task<IReadOnlyList<WardDto>> ListWardsAsync(CancellationToken cancellationToken)
    {

        ApiResponse<WardDto[]>? response = await _wardService.ListAsync(cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true, Data: { } wards } ? wards : [];

    }

    public async Task<IReadOnlyList<ApprenticeSummaryDto>> ListApprenticesAsync(CancellationToken cancellationToken)
    {

        ApiResponse<ListPageResult<ApprenticeSummaryDto>>? response = await _apprenticeService
            .ListAsync(cancellationToken)
            .ConfigureAwait(false);

        return response is { IsSuccess: true, Data: { } page } ? page.Items : [];

    }

    public async Task<IReadOnlyList<McpServerInfo>> ListMcpServersAsync(CancellationToken cancellationToken)
    {

        ApiResponse<McpServerInfo[]>? response = await _mcpService.ListAsync(cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true, Data: { } servers } ? servers : [];

    }

}
