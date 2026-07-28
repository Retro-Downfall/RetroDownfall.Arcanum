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
/// Phases 1–5 are all implemented: shared foundation, session semantic search, semantic codebase
/// retrieval (see <see cref="Codebase"/>), Saga long-term associative memory (see <see cref="Saga"/>),
/// and embedding-based semantic spell routing (see <see cref="SpellRoutingHybridMode"/> and
/// <see cref="SpellRoutingHybridTopK"/>).
/// </summary>
public sealed record EmbeddingSettings
{

    /// <summary>
    /// Master toggle for all RAG features. When <c>false</c> (default), every RAG code path
    /// short-circuits to existing (pre-RAG) behavior.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Provider name (matching an entry in <see cref="ArcanumSettings.Providers"/>) used for embedding
    /// generation. When null/empty and <see cref="Enabled"/> is true, startup validation fails.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// Embedding model name (e.g. <c>nomic-embed-text</c>, <c>text-embedding-3-small</c>). When
    /// null/empty and <see cref="Enabled"/> is true, startup validation fails.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Expected embedding vector dimension. Must match the configured model's output. Used for the
    /// vec0 acceleration table schema. Default <c>768</c>; clamped 64–4,096 at runtime. Changing this
    /// after data exists requires an operator-triggered re-index (see DESIGN.md §21).
    /// </summary>
    public int Dimensions { get; set; } = 768;

    /// <summary>
    /// Maximum texts per embedding API call. Default <c>32</c>; clamped 1–256 at runtime.
    /// </summary>
    public int BatchSize { get; set; } = 32;

    /// <summary>
    /// Maximum characters per chunk when embedding long documents. Default <c>1000</c>; clamped
    /// 128–8,192 at runtime.
    /// </summary>
    public int ChunkSizeChars { get; set; } = 1000;

    /// <summary>
    /// Overlap in characters between adjacent chunks. Default <c>100</c>; clamped 0–1,024 at runtime.
    /// </summary>
    public int ChunkOverlapChars { get; set; } = 100;

    /// <summary>
    /// Minimum cosine similarity for a Divination result to be included. Default <c>0.70</c>; clamped
    /// 0.0–1.0 at runtime.
    /// </summary>
    public float SimilarityThreshold { get; set; } = 0.70f;

    /// <summary>
    /// Default maximum results per retrieval call. Individual features may override. Default <c>5</c>;
    /// clamped 1–50 at runtime.
    /// </summary>
    public int MaxResults { get; set; } = 5;

    /// <summary>
    /// Timeout in seconds for embedding API calls. Default <c>30</c>; clamped 5–300 at runtime.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum total UTF-8 character count across all inputs in a single <c>POST /v1/embeddings</c>
    /// request. Default <c>1,000,000</c>; clamped 1,000–10,000,000 at runtime. Exceeding this returns
    /// <c>400 invalid_request_error</c> — distinct from <see cref="ChunkSizeChars"/>, which bounds a
    /// single string sent to the provider per call (oversized single inputs are chunked, not rejected).
    /// </summary>
    public int MaxEmbeddingInputChars { get; set; } = 1_000_000;

    /// <summary>
    /// Feature flag for Phase 2 (session semantic search / Divination over the Grimoire). When
    /// <c>true</c>, <see cref="Enabled"/> must also be <c>true</c> (enforced by
    /// <see cref="ConfigurationValidator"/>).
    /// </summary>
    public bool SessionSearchEnabled { get; set; }

    /// <summary>
    /// RAG Phase 2 — interval, in seconds, between <c>EntryWeavingService</c> embedding queue
    /// processing ticks (imprinting not-yet-embedded Grimoire entries into <c>entry_embeddings</c>).
    /// Default <c>10</c>; clamped 1–300 at runtime. Only relevant when <see cref="SessionSearchEnabled"/>
    /// is <c>true</c> — the service idles otherwise.
    /// </summary>
    public int EmbeddingQueueIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// Feature flag for Phase 3 (semantic codebase retrieval). When <c>true</c>, <see cref="Enabled"/>
    /// must also be <c>true</c> (enforced by <see cref="ConfigurationValidator"/>).
    /// </summary>
    public bool CodebaseRetrievalEnabled { get; set; }

    /// <summary>
    /// RAG Phase 3 — semantic codebase retrieval tuning (file indexing bounds, extensions, background
    /// re-index interval, and per-turn retrieval cap). Only relevant when
    /// <see cref="CodebaseRetrievalEnabled"/> is <c>true</c>.
    /// </summary>
    public CodebaseEmbeddingSettings Codebase { get; set; } = new();

    /// <summary>
    /// Feature flag for Phase 4 (Saga — long-term associative memory). When <c>true</c>,
    /// <see cref="Enabled"/> must also be <c>true</c> (enforced by <see cref="ConfigurationValidator"/>).
    /// </summary>
    public bool SagaEnabled { get; set; }

    /// <summary>
    /// RAG Phase 4 — Saga (long-term associative memory) tuning (extraction cadence, caps, model, and
    /// window size). Only relevant when <see cref="SagaEnabled"/> is <c>true</c>.
    /// </summary>
    public SagaEmbeddingSettings Saga { get; set; } = new();

    /// <summary>
    /// Feature flag for Phase 5 (embedding-based spell routing pre-filter). When <c>false</c> (default),
    /// the existing LLM-based <c>SemanticRouter</c> is used unchanged. When <c>true</c>,
    /// <see cref="Enabled"/> must also be <c>true</c> (enforced by <see cref="ConfigurationValidator"/>).
    /// </summary>
    public bool SemanticSpellRoutingEnabled { get; set; }

    /// <summary>
    /// RAG Phase 5 — when <c>true</c> and <see cref="SemanticSpellRoutingEnabled"/> is also <c>true</c>,
    /// embedding similarity pre-filters the spell catalog to the top
    /// <see cref="SpellRoutingHybridTopK"/> candidates before the existing LLM-based
    /// <c>SemanticRouter</c> picks from that reduced set (hybrid mode). When <c>false</c> (default),
    /// the highest-similarity spell above <see cref="SimilarityThreshold"/> wins outright with no LLM
    /// call (pure embedding mode).
    /// </summary>
    public bool SpellRoutingHybridMode { get; set; }

    /// <summary>
    /// RAG Phase 5 — number of top candidates passed to the LLM router in hybrid mode. Default
    /// <c>3</c>; clamped 1–20 at runtime. Only relevant when <see cref="SemanticSpellRoutingEnabled"/>
    /// and <see cref="SpellRoutingHybridMode"/> are both <c>true</c>.
    /// </summary>
    public int SpellRoutingHybridTopK { get; set; } = 3;

}
