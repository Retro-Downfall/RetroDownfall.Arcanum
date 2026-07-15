using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.TheForge;

public sealed record CampaignExportDto(
    CampaignDto Campaign,
    IReadOnlyList<CampaignExportSpellDto> Spells,
    IReadOnlyList<PromptExportDto> Prompts);

/// <summary>
/// Spell payload inside a campaign export bundle.
/// <see cref="SpellJson"/> is the canonical wire property (SPELL.json sidecar metadata as a JSON string).
/// <see cref="SkillJson"/> is a legacy wire alias accepted on import only and omitted when null on export.
/// </summary>
public sealed record CampaignExportSpellDto(
    string Name,
    string? SpellJson,
    string FullContent,
    IReadOnlyList<CampaignExportScriptDto> Scripts,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SkillJson = null)
{

    /// <summary>Canonical <see cref="SpellJson"/>, or legacy <see cref="SkillJson"/> when the former is absent.</summary>
    [JsonIgnore]
    public string? ResolvedSpellJson =>
        !string.IsNullOrWhiteSpace(SpellJson) ? SpellJson : SkillJson;

}

public sealed record CampaignExportScriptDto(
    string FileName,
    string Base64Content);
