namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Persistent campaign registry path containment and capacity.
/// </summary>
public sealed record CampaignsSettings
{

    /// <summary>
    /// Optional list of absolute directory roots that campaign registration may use.
    /// When empty (default), any existing directory the process can access is permitted.
    /// </summary>
    public string[] AllowedRoots { get; init; } = [];

    /// <summary>
    /// Maximum number of registered campaigns in the Grimoire database.
    /// </summary>
    public int MaxCampaigns { get; init; } = 500;

}
