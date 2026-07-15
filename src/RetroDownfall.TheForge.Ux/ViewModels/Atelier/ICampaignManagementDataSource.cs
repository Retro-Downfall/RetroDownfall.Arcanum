using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Ux.ViewModels;

namespace RetroDownfall.TheForge.Ux.ViewModels.Atelier;

/// <summary>
/// Thin seam over <see cref="Services.Services.CampaignService"/> and
/// <see cref="Services.Services.ExportImportService"/> for Atelier campaign CRUD and import/export.
/// API-client-only — no Grimoire or disk bypass.
/// </summary>
public interface ICampaignManagementDataSource
{

    Task<DataSourceResult<CampaignDto>> CreateAsync(RegisterCampaignRequest request, CancellationToken cancellationToken);

    Task<DataSourceResult<CampaignDto>> UpdateAsync(Guid id, UpdateCampaignRequest request, CancellationToken cancellationToken);

    Task<DataSourceResult<bool>> DeleteAsync(Guid id, CancellationToken cancellationToken);

    Task<DataSourceResult<CampaignExportDto>> ExportAsync(Guid campaignId, CancellationToken cancellationToken);

    Task<DataSourceResult<CampaignImportResultDto>> ImportAsync(
        Guid campaignId,
        string strategy,
        CampaignExportDto payload,
        CancellationToken cancellationToken);

}
