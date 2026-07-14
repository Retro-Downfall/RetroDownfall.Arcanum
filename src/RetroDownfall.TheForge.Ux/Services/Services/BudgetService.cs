using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Serialization;

namespace RetroDownfall.TheForge.Ux.Services.Services;

/// <summary>Wraps <c>GET /api/budget</c> for The Treasury and The Anvil's spend readout.</summary>
public sealed class BudgetService
{

    private readonly ArcanumApiClient _apiClient;

    public BudgetService(ArcanumApiClient apiClient)
    {

        _apiClient = apiClient;

    }

    public Task<ApiResponse<BudgetSummaryDto>?> GetBudgetAsync(CancellationToken cancellationToken) =>
        _apiClient.GetAsync("/api/budget", TheForgeJsonContext.Default.ApiResponseBudgetSummaryDto, cancellationToken);

}
