using RetroDownfall.Arcanum.Core.Workspaces;

namespace RetroDownfall.Arcanum.Core.TheForge;

public sealed record CampaignDto(
    Guid Id,
    string Name,
    string Path,
    WorkspaceType Type,
    string? Description,
    CampaignSettings Settings,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
