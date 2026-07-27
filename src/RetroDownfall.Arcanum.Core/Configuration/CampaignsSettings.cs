namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Campaign runtime projection. Path authority comes from
/// <c>Arcanum:Security:CampaignRoots</c>; registry capacity is a code-owned invariant.
/// </summary>
public sealed record CampaignsSettings
{

    /// <summary>
    /// Effective list of absolute directory roots that campaign registration may use, projected from
    /// <c>Arcanum:Security:CampaignRoots</c>.
    /// An empty array denies all access by default (secure-by-default via
    /// <see cref="WorkspaceRootPolicy"/>). When non-empty, resolved paths must fall under
    /// one of these roots.
    /// </summary>
    public string[] AllowedRoots { get; set; } = [];

    /// <summary>
    /// Maximum number of registered campaigns in the Grimoire database.
    /// </summary>
    public int MaxCampaigns { get; set; } = 500;

}
