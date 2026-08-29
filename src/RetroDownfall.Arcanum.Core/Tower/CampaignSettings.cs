namespace RetroDownfall.Arcanum.Core.Tower;

/// <summary>
/// One Campaign's settings: the wire shape for <c>PUT /api/campaigns/{id}</c>, for the export/import
/// bundle, and for the persisted <c>Settings</c> column.
/// </summary>
public sealed record CampaignSettings(
    string? DefaultModel,
    Dictionary<string, string>? ModelMap,
    List<string>? McpServerProfiles,
    List<string>? SpellRoots,
    string? LoreNamespace,
    List<string>? AllowedTools)
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
            AllowedTools: null);

}
