using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Ux.Services.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.Atelier;

/// <summary>API-backed campaign management for Atelier CRUD and import/export.</summary>
public sealed class CampaignManagementDataSource : ICampaignManagementDataSource
{

    private readonly CampaignService _campaignService;

    private readonly ExportImportService _exportImportService;

    public CampaignManagementDataSource(CampaignService campaignService, ExportImportService exportImportService)
    {

        _campaignService = campaignService;

        _exportImportService = exportImportService;

    }

    public async Task<DataSourceResult<CampaignDto>> CreateAsync(
        RegisterCampaignRequest request,
        CancellationToken cancellationToken)
    {

        ApiResponse<CampaignDto>? response = await _campaignService
            .CreateAsync(request, cancellationToken)
            .ConfigureAwait(false);

        return DataSourceResult<CampaignDto>.FromResponse(response);

    }

    public async Task<DataSourceResult<CampaignDto>> UpdateAsync(
        Guid id,
        UpdateCampaignRequest request,
        CancellationToken cancellationToken)
    {

        ApiResponse<CampaignDto>? response = await _campaignService
            .UpdateAsync(id, request, cancellationToken)
            .ConfigureAwait(false);

        return DataSourceResult<CampaignDto>.FromResponse(response);

    }

    public async Task<DataSourceResult<bool>> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {

        bool deleted = await _campaignService.DeleteAsync(id, cancellationToken).ConfigureAwait(false);

        if (deleted)
        {

            return new DataSourceResult<bool>(true, true, null, null);

        }

        return new DataSourceResult<bool>(
            false,
            false,
            ErrorCodes.Campaign.NotFound,
            "Campaign unregister failed.");

    }

    public async Task<DataSourceResult<CampaignExportDto>> ExportAsync(
        Guid campaignId,
        CancellationToken cancellationToken)
    {

        ApiResponse<CampaignExportDto>? response = await _exportImportService
            .ExportCampaignAsync(campaignId, cancellationToken)
            .ConfigureAwait(false);

        return DataSourceResult<CampaignExportDto>.FromResponse(response);

    }

    public async Task<DataSourceResult<CampaignImportResultDto>> ImportAsync(
        Guid campaignId,
        string strategy,
        CampaignExportDto payload,
        CancellationToken cancellationToken)
    {

        ApiResponse<CampaignImportResultDto>? response = await _exportImportService
            .ImportCampaignAsync(campaignId, strategy, payload, cancellationToken)
            .ConfigureAwait(false);

        return DataSourceResult<CampaignImportResultDto>.FromResponse(response);

    }

}
