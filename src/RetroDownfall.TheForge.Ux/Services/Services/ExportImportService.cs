using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Core.Serialization;

namespace RetroDownfall.TheForge.Ux.Services.Services;

/// <summary>Wraps <c>POST /api/campaigns/{id}/export</c> and <c>POST /api/campaigns/{id}/import</c> for the Export / Import Wizard.</summary>
public sealed class ExportImportService
{

    private readonly ArcanumApiClient _apiClient;

    public ExportImportService(ArcanumApiClient apiClient)
    {

        _apiClient = apiClient;

    }

    public Task<ApiResponse<CampaignExportDto>?> ExportCampaignAsync(Guid campaignId, CancellationToken cancellationToken) =>
        _apiClient.PostAsync($"/api/campaigns/{campaignId}/export", ForgeJsonContext.Default.ApiResponseCampaignExportDto, cancellationToken);

    public Task<ApiResponse<CampaignImportResultDto>?> ImportCampaignAsync(
        Guid campaignId,
        string strategy,
        CampaignExportDto? payload,
        CancellationToken cancellationToken) =>
        _apiClient.PostAsync(
            $"/api/campaigns/{campaignId}/import",
            new CampaignImportRequest(strategy, payload),
            ForgeJsonContext.Default.CampaignImportRequest,
            ForgeJsonContext.Default.ApiResponseCampaignImportResultDto,
            cancellationToken);

}
