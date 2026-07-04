namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// RAG Phase 1 — The Weave &amp; Divination shared foundation. Bound from <c>Arcanum:Embeddings</c>.
///
/// "The Weave" is Arcanum's embedding and vector substrate; "Divination" is semantic search over it.
/// This record carries the shared embedding-generation knobs plus the per-phase feature flags. Every
/// RAG code path must check <see cref="Enabled"/> (and, for its own feature, the matching flag below)
/// before doing any embedding or vector work — when either is <c>false</c>, behavior is unchanged from
/// pre-RAG Arcanum (graceful degradation, never a functional regression).
///
/// Phase 1 scope only: this record intentionally does not yet carry the nested <c>Saga</c> or
/// <c>Codebase</c> sub-records described for Phases 3/4 of the RAG design — those arrive with their
/// own phases so the Compendium setting-descriptor coverage walk never sees orphaned,
/// not-yet-implemented settings.
/// </summary>
public sealed record EmbeddingSettings
{

    /// <summary>
    /// Master toggle for all RAG features. When <c>false</c> (default), every RAG code path
    /// short-circuits to existing (pre-RAG) behavior.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Provider name (matching an entry in <see cref="ArcanumSettings.Providers"/>) used for embedding
    /// generation. When null/empty and <see cref="Enabled"/> is true, startup validation fails.
    /// </summary>
    public string? Provider { get; init; }

    /// <summary>
    /// Embedding model name (e.g. <c>nomic-embed-text</c>, <c>text-embedding-3-small</c>). When
    /// null/empty and <see cref="Enabled"/> is true, startup validation fails.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Expected embedding vector dimension. Must match the configured model's output. Used for the
    /// vec0 acceleration table schema. Default <c>768</c>; clamped 64–4,096 at runtime. Changing this
    /// after data exists requires an operator-triggered re-index (see DESIGN.md §21).
    /// </summary>
    public int Dimensions { get; init; } = 768;

    /// <summary>
    /// Maximum texts per embedding API call. Default <c>32</c>; clamped 1–256 at runtime.
    /// </summary>
    public int BatchSize { get; init; } = 32;

    /// <summary>
    /// Maximum characters per chunk when embedding long documents. Default <c>1000</c>; clamped
    /// 128–8,192 at runtime.
    /// </summary>
    public int ChunkSizeChars { get; init; } = 1000;

    /// <summary>
    /// Overlap in characters between adjacent chunks. Default <c>100</c>; clamped 0–1,024 at runtime.
    /// </summary>
    public int ChunkOverlapChars { get; init; } = 100;

    /// <summary>
    /// Minimum cosine similarity for a Divination result to be included. Default <c>0.70</c>; clamped
    /// 0.0–1.0 at runtime.
    /// </summary>
    public float SimilarityThreshold { get; init; } = 0.70f;

    /// <summary>
    /// Default maximum results per retrieval call. Individual features may override. Default <c>5</c>;
    /// clamped 1–50 at runtime.
    /// </summary>
    public int MaxResults { get; init; } = 5;

    /// <summary>
    /// Timeout in seconds for embedding API calls. Default <c>30</c>; clamped 5–300 at runtime.
    /// </summary>
    public int RequestTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Feature flag for Phase 2 (session semantic search / Divination over the Grimoire). When
    /// <c>true</c>, <see cref="Enabled"/> must also be <c>true</c> (enforced by
    /// <see cref="ConfigurationValidator"/>).
    /// </summary>
    public bool SessionSearchEnabled { get; init; }

    /// <summary>
    /// Feature flag for Phase 3 (semantic codebase retrieval). When <c>true</c>, <see cref="Enabled"/>
    /// must also be <c>true</c> (enforced by <see cref="ConfigurationValidator"/>).
    /// </summary>
    public bool CodebaseRetrievalEnabled { get; init; }

    /// <summary>
    /// Feature flag for Phase 4 (Saga — long-term associative memory). When <c>true</c>,
    /// <see cref="Enabled"/> must also be <c>true</c> (enforced by <see cref="ConfigurationValidator"/>).
    /// </summary>
    public bool SagaEnabled { get; init; }

    /// <summary>
    /// Feature flag for Phase 5 (embedding-based spell routing pre-filter). When <c>false</c> (default),
    /// the existing LLM-based <c>SemanticRouter</c> is used unchanged. When <c>true</c>,
    /// <see cref="Enabled"/> must also be <c>true</c> (enforced by <see cref="ConfigurationValidator"/>).
    /// </summary>
    public bool SemanticSpellRoutingEnabled { get; init; }

}
