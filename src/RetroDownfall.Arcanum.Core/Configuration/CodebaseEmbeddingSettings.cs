namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// RAG Phase 3 — code-owned semantic codebase retrieval mechanics. Only relevant when
/// <c>Arcanum:Features:CodebaseRetrieval</c> is enabled together with
/// <c>Arcanum:Features:Embeddings</c>.
/// </summary>
public sealed record CodebaseEmbeddingSettings
{

    /// <summary>
    /// Maximum files to embed per workspace during a single indexing tick. Default <c>500</c>; clamped
    /// 1–10,000 at runtime.
    /// </summary>
    public int MaxFilesToIndex { get; set; } = 500;

    /// <summary>
    /// File extensions (including the leading dot) eligible for indexing, matched case-insensitively.
    /// An empty array means nothing is indexed.
    /// </summary>
    public string[] FileExtensions { get; set; } =
    [
        ".cs",
        ".py",
        ".js",
        ".ts",
        ".go",
        ".rs",
        ".java",
        ".md",
        ".txt",
        ".json",
        ".yaml",
        ".yml",
    ];

    /// <summary>
    /// Background re-indexing interval, in minutes, for workspaces with active inference (see
    /// <c>WorkspaceIndexingService</c>). Default <c>60</c>; clamped 5–1,440 at runtime.
    /// </summary>
    public int IndexingIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Debounce window for coalescing filesystem watcher events. Default <c>300</c> milliseconds;
    /// clamped 50–5,000 at runtime.
    /// </summary>
    public int WatcherDebounceMilliseconds { get; set; } = 300;

    /// <summary>
    /// Maximum workspaces watched concurrently with one recursive watcher per workspace. Default
    /// <c>32</c>; clamped 0–128. Zero leaves periodic reconciliation as the only indexing trigger.
    /// </summary>
    public int MaxWatchers { get; set; } = 32;

    /// <summary>
    /// Periodic full reconciliation interval used as a correctness fallback for lost/unavailable
    /// watcher events. Default <c>60</c> minutes; clamped 1–1,440 at runtime.
    /// </summary>
    public int ReconciliationIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Maximum retrieved file chunks injected into the system prompt per inference turn. Default
    /// <c>5</c>; clamped 1–50 at runtime.
    /// </summary>
    public int MaxRetrievedChunks { get; set; } = 5;

}
