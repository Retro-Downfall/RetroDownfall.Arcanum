namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// RAG Phase 4 — Saga (long-term associative memory) sub-settings. Bound from
/// <c>Arcanum:Embeddings:Saga</c>. Only relevant when <c>Arcanum:Embeddings:SagaEnabled</c> is
/// <c>true</c> (which itself requires <c>Arcanum:Embeddings:Enabled</c>) — see
/// <see cref="EmbeddingSettings"/>.
/// </summary>
public sealed record SagaEmbeddingSettings
{

    /// <summary>
    /// When <see cref="EmbeddingSettings.SagaEnabled"/> is <c>true</c>, controls whether the
    /// background <c>SagaExtractionService</c> runs. Setting this to <c>false</c> allows
    /// retrieval-only mode — existing memories are still surfaced during inference, but no new
    /// memories are extracted. Default <c>true</c>.
    /// </summary>
    public bool ExtractionEnabled { get; set; } = true;

    /// <summary>
    /// Maximum Saga memories associated with a single session. New extractions for a session at
    /// this cap are rejected (logged as a warning). Default <c>50</c>; clamped 1–1,000 at runtime.
    /// </summary>
    public int MaxMemoriesPerSession { get; set; } = 50;

    /// <summary>
    /// Maximum total Saga memories across all sessions. New extractions are rejected once this cap
    /// is reached. Default <c>10,000</c>; clamped 100–1,000,000 at runtime.
    /// </summary>
    public int MaxMemoriesTotal { get; set; } = 10_000;

    /// <summary>
    /// Model used for memory extraction. When null/empty, falls back to
    /// <see cref="ArcanumSettings.FastModel"/>, then <see cref="ArcanumSettings.DefaultModel"/>.
    /// Default <c>null</c>.
    /// </summary>
    public string? ExtractionModel { get; set; }

    /// <summary>
    /// Maximum output tokens for the extraction LLM call. Default <c>500</c>; clamped 100–4,096 at
    /// runtime.
    /// </summary>
    public int ExtractionMaxTokens { get; set; } = 500;

    /// <summary>
    /// Interval, in minutes, that the extraction queue is expected to be processed against — informs
    /// operator-facing documentation and any periodic health checks. The extraction service itself is
    /// event-driven (enqueued after successful inference turns), not polling. Default <c>15</c>;
    /// clamped 1–1,440 at runtime.
    /// </summary>
    public int ExtractionIntervalMinutes { get; set; } = 15;

    /// <summary>
    /// Number of recent Grimoire entries reviewed per extraction call. Default <c>10</c>; clamped
    /// 2–50 at runtime.
    /// </summary>
    public int ExtractionWindowEntries { get; set; } = 10;

}
