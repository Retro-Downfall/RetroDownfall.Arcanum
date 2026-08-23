using RetroDownfall.Arcanum.Core.Workspaces;

namespace RetroDownfall.Arcanum.Core.Tower;

public sealed record RegisterCampaignRequest(
    string Name,
    string Path,
    WorkspaceType Type,
    string? Description);
