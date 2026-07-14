namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Fallback entity-extraction preflight JSON contract for The Lexicon read path. The model returns
/// a JSON object whose <c>entities</c> array carries short subject/noun strings mentioned in the
/// user prompt; null/missing maps to an empty array.
/// </summary>
public sealed record LexiconEntityExtractionResponse(
    [property: System.Text.Json.Serialization.JsonPropertyName("entities")] string[]? Entities);
