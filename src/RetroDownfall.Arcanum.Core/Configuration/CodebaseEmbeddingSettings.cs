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
    /// Files larger than this (in characters) are skipped during indexing. Default <c>50,000</c>;
    /// clamped 1,000–500,000 at runtime.
    /// </summary>
    public int MaxFileSizeChars { get; set; } = 50_000;

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
    /// Maximum retrieved file chunks injected into the system prompt per inference turn. Default
    /// <c>5</c>; clamped 1–50 at runtime.
    /// </summary>
    public int MaxRetrievedChunks { get; set; } = 5;

}
