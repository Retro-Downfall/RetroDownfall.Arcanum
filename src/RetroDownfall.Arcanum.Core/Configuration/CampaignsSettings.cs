namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Persistent campaign registry path containment and capacity.
/// </summary>
public sealed record CampaignsSettings
{

    /// <summary>
    /// Optional list of absolute directory roots that campaign registration may use.
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
