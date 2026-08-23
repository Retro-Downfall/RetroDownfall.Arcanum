using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.TheForge.Ux.Services;
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

        DeleteOutcome outcome = await _campaignService.DeleteAsync(id, cancellationToken).ConfigureAwait(false);

        if (outcome.Success)
        {

            return new DataSourceResult<bool>(true, true, null, null);

        }

        // A 404 really is "not registered"; a refused connection, a 403, or a 500 is not, and
        // labelling all three Campaign.NotFound sends the operator looking for the wrong problem.
        return new DataSourceResult<bool>(
            false,
            false,
            outcome.ErrorCode == "Http.404" ? ErrorCodes.Campaign.NotFound : outcome.ErrorCode,
            outcome.ErrorMessage ?? "Campaign unregister failed.");

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
