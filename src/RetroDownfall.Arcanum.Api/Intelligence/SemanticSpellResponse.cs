namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// The SemanticRouter preflight JSON contract. <c>spellName</c> selects the active spell (or
/// <c>NONE</c>); <c>entities</c> carries subject/noun strings extracted from the user prompt for
/// Lexicon memory retrieval. <c>entities</c> may be null/empty and survives a <c>NONE</c> spell.
/// </summary>
public sealed record SemanticSpellResponse(
    [property: System.Text.Json.Serialization.JsonPropertyName("spellName")] string SpellName,
    [property: System.Text.Json.Serialization.JsonPropertyName("entities")] string[]? Entities);
