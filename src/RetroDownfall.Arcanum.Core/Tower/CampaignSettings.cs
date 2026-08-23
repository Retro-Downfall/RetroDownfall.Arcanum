namespace RetroDownfall.Arcanum.Core.Tower;

/// <summary>
/// One Campaign's settings: the wire shape for <c>PUT /api/campaigns/{id}</c>, for the export/import
/// bundle, and for the persisted <c>Settings</c> column.
/// </summary>
/// <remarks>
/// <para><see cref="RequireWardForForbiddenArts"/> carries its default on the constructor itself, not
/// only in <see cref="CreateDefault"/>. A positional member with no default binds to <c>default(T)</c>
/// when the JSON omits it, so a hand-composed payload that said nothing about the Ward used to arrive as
/// <see langword="false"/> and was persisted wholesale — silently removing the operator-consent gate from
/// every non-intrinsic forbidden art. Absence has to mean warded; opting out has to be said out loud.</para>
/// </remarks>
public sealed record CampaignSettings(
    string? DefaultModel,
    Dictionary<string, string>? ModelMap,
    List<string>? McpServerProfiles,
    List<string>? SpellRoots,
    string? LoreNamespace,
    List<string>? AllowedTools,
    bool RequireWardForForbiddenArts = true)
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
