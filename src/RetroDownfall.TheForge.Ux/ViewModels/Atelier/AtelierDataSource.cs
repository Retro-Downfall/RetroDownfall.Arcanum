using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.TheForge.Ux.Services.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.Atelier;

/// <summary>Production data source for The Atelier, composed from the thin per-route API services.</summary>
public sealed class AtelierDataSource : IAtelierDataSource
{

    private readonly CampaignService _campaignService;

    private readonly WorkspaceService _workspaceService;

    private readonly SpellService _spellService;

    private readonly SessionService _sessionService;

    public AtelierDataSource(
        CampaignService campaignService,
        WorkspaceService workspaceService,
        SpellService spellService,
        SessionService sessionService)
    {

        _campaignService = campaignService;

        _workspaceService = workspaceService;

        _spellService = spellService;

        _sessionService = sessionService;

    }

    public async Task<IReadOnlyList<CampaignDto>> GetCampaignsAsync(CancellationToken cancellationToken)
    {

        ApiResponse<ListPageResult<CampaignDto>>? response = await _campaignService
            .ListAsync(type: null, limit: 10_000, offset: 0, cancellationToken)
            .ConfigureAwait(false);

        return response?.Data?.Items ?? [];

    }

    public async Task<IReadOnlyList<WorkspaceInfo>> GetWorkspacesAsync(CancellationToken cancellationToken)
    {

        ApiResponse<WorkspaceInfo[]>? response = await _workspaceService
            .ListAsync(cancellationToken)
            .ConfigureAwait(false);

        return response?.Data ?? [];

    }

    public async Task<IReadOnlyList<SpellSummary>> GetGlobalSpellsAsync(CancellationToken cancellationToken)
    {

        ApiResponse<SpellSummary[]>? response = await _spellService
            .ListAsync(workspace: null, cancellationToken)
            .ConfigureAwait(false);

        return response?.Data ?? [];

    }

    public async Task<IReadOnlyList<SessionSummaryDto>> GetRecentSessionsAsync(CancellationToken cancellationToken)
    {

        ApiResponse<SessionQueryResult>? response = await _sessionService
            .QueryAsync(campaignId: null, status: null, search: null, limit: 20, beforeUpdatedAt: null, cancellationToken)
            .ConfigureAwait(false);

        return response?.Data?.Summaries ?? [];

    }

    public async Task<IReadOnlyList<SpellSummary>> GetCampaignSpellsAsync(Guid campaignId, CancellationToken cancellationToken)
    {

        ApiResponse<SpellSummary[]>? response = await _spellService
            .GetCampaignSpellsAsync(campaignId, query: null, tag: null, tool: null, cancellationToken)
            .ConfigureAwait(false);

        return response?.Data ?? [];

    }

    public async Task<IReadOnlyList<PromptSummaryDto>> GetCampaignPromptsAsync(Guid campaignId, CancellationToken cancellationToken)
    {

        ApiResponse<ListPageResult<PromptSummaryDto>>? response = await _campaignService
            .GetPromptsAsync(campaignId, query: null, tag: null, cancellationToken)
            .ConfigureAwait(false);

        return response?.Data?.Items ?? [];

    }

    public async Task<IReadOnlyList<SessionSummaryDto>> GetCampaignSessionsAsync(Guid campaignId, CancellationToken cancellationToken)
    {

        ApiResponse<SessionQueryResult>? response = await _campaignService
            .GetSessionsAsync(campaignId, status: null, search: null, limit: 20, beforeUpdatedAt: null, cancellationToken)
            .ConfigureAwait(false);

        return response?.Data?.Summaries ?? [];

    }

}
