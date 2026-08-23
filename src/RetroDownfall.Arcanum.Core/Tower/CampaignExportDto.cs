using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Tower;

/// <summary>
/// One Campaign's spells, prompts, and settings, plus what the export deliberately left behind.
/// </summary>
/// <remarks>
/// <see cref="Exclusions"/> is omitted entirely when this installation has no Covenant arm, so a
/// bundle produced with <c>Arcanum:Features:Covenant</c> off is byte-for-byte what it always was. A
/// zero report would be worse than an absent one: it reads as a measurement where the honest answer
/// is that nothing was measured (§10.19.11).
/// </remarks>
public sealed record CampaignExportDto(
    CampaignDto Campaign,
    IReadOnlyList<CampaignExportSpellDto> Spells,
    IReadOnlyList<PromptExportDto> Prompts,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    CampaignExportExclusionsDto? Exclusions = null);

/// <summary>
/// The content-free counts of what a Campaign export did not carry.
/// </summary>
/// <remarks>
/// Two counts, never folded into one. A canonical entry is Covenant memory the operator authored and
/// this bundle does not include; a tainted artifact is some other object of this Campaign's that is
/// Covenant-derived. An operator deciding whether the bundle is a complete transfer needs to know
/// which of the two it is short of.
/// </remarks>
public sealed record CampaignExportExclusionsDto(
    long CovenantEntryCount,
    long TaintedArtifactCount);

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
