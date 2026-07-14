using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.TheForge.Core.Serialization;

namespace RetroDownfall.TheForge.Ux.Services.Services;

/// <summary>
/// Wraps <c>GET/PUT /api/campaigns/{id}/sanctum</c> for the Sanctum Breach Monitor's config surface.
/// Breach browsing (<c>GET /api/campaigns/{id}/sanctum/breaches</c>) returns <c>Api.Models</c>-only
/// DTOs (<c>SanctumBreachDto</c>/<c>SanctumBreachQueryResult</c>) and is deferred until that UI phase
/// lands, to avoid re-declaring more types than the alpha needs.
/// </summary>
public sealed class SanctumService
{

    private readonly ArcanumApiClient _apiClient;

    public SanctumService(ArcanumApiClient apiClient)
    {

        _apiClient = apiClient;

    }

    public Task<ApiResponse<SanctumConfig>?> GetConfigAsync(Guid campaignId, CancellationToken cancellationToken) =>
        _apiClient.GetAsync($"/api/campaigns/{campaignId}/sanctum", TheForgeJsonContext.Default.ApiResponseSanctumConfig, cancellationToken);

    public Task<ApiResponse<SanctumConfig>?> UpdateConfigAsync(Guid campaignId, SanctumConfig config, CancellationToken cancellationToken) =>
        _apiClient.PutAsync(
            $"/api/campaigns/{campaignId}/sanctum",
            config,
            TheForgeJsonContext.Default.SanctumConfig,
            TheForgeJsonContext.Default.ApiResponseSanctumConfig,
            cancellationToken);

}
