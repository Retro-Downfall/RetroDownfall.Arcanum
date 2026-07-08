using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Workspaces;

namespace RetroDownfall.TheForge.Ux.ViewModels.Atelier;

/// <summary>
/// Data-source seam for The Atelier tree. Production implementation delegates to the per-route
/// services; tests use a simple fake so node composition and navigation are verified without HTTP.
/// </summary>
public interface IAtelierDataSource
{

    Task<IReadOnlyList<CampaignDto>> GetCampaignsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkspaceInfo>> GetWorkspacesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<SpellSummary>> GetGlobalSpellsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<SessionSummaryDto>> GetRecentSessionsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<SpellSummary>> GetCampaignSpellsAsync(Guid campaignId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PromptSummaryDto>> GetCampaignPromptsAsync(Guid campaignId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SessionSummaryDto>> GetCampaignSessionsAsync(Guid campaignId, CancellationToken cancellationToken);

}
