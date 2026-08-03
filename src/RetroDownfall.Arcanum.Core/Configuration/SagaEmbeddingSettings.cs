namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// RAG Phase 4 — code-owned Saga (long-term associative memory) mechanics. Only relevant when
/// Saga is enabled directly or derived from <c>Arcanum:Features:SagaExtraction</c>; either opt-in
/// also derives the embeddings substrate — see <see cref="EmbeddingSettings"/>.
/// </summary>
public sealed record SagaEmbeddingSettings
{

    /// <summary>
    /// Controls whether the background <c>SagaExtractionService</c> runs. Enabling extraction
    /// derives <see cref="EmbeddingSettings.SagaEnabled"/> and the embeddings substrate; leaving it
    /// disabled allows retrieval-only mode when Saga is enabled directly.
    /// </summary>
    public bool ExtractionEnabled { get; set; } = true;

    /// <summary>
    /// Model used for memory extraction. When null/empty, falls back to
    /// <see cref="ArcanumSettings.FastModel"/>, then <see cref="ArcanumSettings.DefaultModel"/>.
    /// Default <c>null</c>.
    /// </summary>
    public string? ExtractionModel { get; set; }

}
