using RetroDownfall.Arcanum.Core.Workspaces;

namespace RetroDownfall.Arcanum.Core.TheForge;

public sealed record UpdateCampaignRequest(
    string? Name,
    WorkspaceType? Type,
    string? Description,
    CampaignSettings? Settings);
