using RetroDownfall.Arcanum.Core.Workspaces;

namespace RetroDownfall.Arcanum.Core.Tower;

public sealed record UpdateCampaignRequest(
    string? Name,
    WorkspaceType? Type,
    string? Description,
    CampaignSettings? Settings);
