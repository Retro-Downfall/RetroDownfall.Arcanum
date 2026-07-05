namespace RetroDownfall.Arcanum.Core.Weave;

/// <summary>
/// RAG Phase 4 — the JSON shape <c>SagaExtractionService</c> asks the extraction LLM to produce.
/// Deserialized via <c>TheForgeJsonContext.Default.SagaExtractionResponse</c> (AOT-safe source
/// generation) rather than <see cref="System.Text.Json.JsonDocument"/> — mirrors how
/// <c>SemanticRouter</c> deserializes <c>SemanticSpellResponse</c>.
/// </summary>
public sealed record SagaExtractionResponse(string[] Memories);
