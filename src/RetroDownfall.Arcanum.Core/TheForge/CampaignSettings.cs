namespace RetroDownfall.Arcanum.Core.TheForge;

public sealed record CampaignSettings(
    string? DefaultModel,
    Dictionary<string, string>? ModelMap,
    List<string>? McpServerProfiles,
    List<string>? SpellRoots,
    string? LoreNamespace,
    List<string>? AllowedTools,
    bool RequireWardForForbiddenArts)
{

    /// <summary>
    /// Default settings applied when registering a new campaign (API, workspace registry, export snapshot).
    /// </summary>
    public static CampaignSettings CreateDefault() =>
        new(
            DefaultModel: null,
            ModelMap: null,
            McpServerProfiles: null,
            SpellRoots: null,
            LoreNamespace: null,
            AllowedTools: null,
            RequireWardForForbiddenArts: true);

}
