using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Ux.Services.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.Workbench;

/// <summary>Data-source seam for The Codex editor; tests fake this interface.</summary>
public interface ICodexDataSource
{

    Task<DataSourceResult<CodexContentDto>> GetCampaignCodexAsync(Guid campaignId, CancellationToken cancellationToken);

    Task<DataSourceResult<CodexContentDto>> PutCampaignCodexAsync(Guid campaignId, string content, CancellationToken cancellationToken);

    Task<DataSourceResult<bool>> DeleteCampaignCodexAsync(Guid campaignId, CancellationToken cancellationToken);

    Task<DataSourceResult<CodexContentDto>> GetGlobalCodexAsync(CancellationToken cancellationToken);

    Task<DataSourceResult<CodexContentDto>> PutGlobalCodexAsync(string content, CancellationToken cancellationToken);

    Task<DataSourceResult<bool>> DeleteGlobalCodexAsync(CancellationToken cancellationToken);

}

/// <summary>API-backed <see cref="ICodexDataSource"/> — wraps <see cref="CampaignService"/> for campaign Codex (<c>/api/campaigns/{id}/codex</c>) and the Grimoire-global Codex (<c>/api/codex</c>).</summary>
public sealed class CodexDataSource : ICodexDataSource
{

    private readonly CampaignService _campaignService;

    public CodexDataSource(CampaignService campaignService)
    {

        _campaignService = campaignService;

    }

    public async Task<DataSourceResult<CodexContentDto>> GetCampaignCodexAsync(Guid campaignId, CancellationToken cancellationToken)
    {

        ApiResponse<CodexContentDto>? response = await _campaignService.GetCodexAsync(campaignId, cancellationToken).ConfigureAwait(false);

        return DataSourceResult<CodexContentDto>.FromResponse(response);

    }

    public async Task<DataSourceResult<CodexContentDto>> PutCampaignCodexAsync(Guid campaignId, string content, CancellationToken cancellationToken)
    {

        ApiResponse<CodexContentDto>? response = await _campaignService.PutCodexAsync(campaignId, content, cancellationToken).ConfigureAwait(false);

        return DataSourceResult<CodexContentDto>.FromResponse(response);

    }

    public async Task<DataSourceResult<bool>> DeleteCampaignCodexAsync(Guid campaignId, CancellationToken cancellationToken)
    {

        ApiResponse<bool>? response = await _campaignService.DeleteCodexAsync(campaignId, cancellationToken).ConfigureAwait(false);

        return DataSourceResult<bool>.FromResponse(response);

    }

    public async Task<DataSourceResult<CodexContentDto>> GetGlobalCodexAsync(CancellationToken cancellationToken)
    {

        ApiResponse<CodexContentDto>? response = await _campaignService.GetGlobalCodexAsync(cancellationToken).ConfigureAwait(false);

        return DataSourceResult<CodexContentDto>.FromResponse(response);

    }

    public async Task<DataSourceResult<CodexContentDto>> PutGlobalCodexAsync(string content, CancellationToken cancellationToken)
    {

        ApiResponse<CodexContentDto>? response = await _campaignService.PutGlobalCodexAsync(content, cancellationToken).ConfigureAwait(false);

        return DataSourceResult<CodexContentDto>.FromResponse(response);

    }

    public async Task<DataSourceResult<bool>> DeleteGlobalCodexAsync(CancellationToken cancellationToken)
    {

        ApiResponse<bool>? response = await _campaignService.DeleteGlobalCodexAsync(cancellationToken).ConfigureAwait(false);

        return DataSourceResult<bool>.FromResponse(response);

    }

}
