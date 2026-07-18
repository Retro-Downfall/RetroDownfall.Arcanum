namespace RetroDownfall.Arcanum.Core.Workspaces;

/// <summary>
/// Phase 7 (RAG / The Weave inspector) — the indexing status of a single workspace, returned by
/// <c>GET /api/workspaces/{id}/files/index/status</c>. Read-only; never triggers indexing. Counts and
/// timestamps reflect what is presently persisted in <c>workspace_file_chunks</c>; skipped-file reasons
/// are <em>not</em> persisted by Arcanum and are surfaced honestly via <see cref="SkippedFilesNote"/>
/// rather than synthesized.
/// </summary>
public sealed record WorkspaceIndexStatusDto(
    string WorkspaceId,
    string WorkspaceName,
    string WorkspacePath,
    string VectorMode,
    string VectorDiagnostic,
    bool IndexingEnabled,
    int TotalIndexedFiles,
    int TotalChunks,
    DateTimeOffset? OldestIndexedAt,
    DateTimeOffset? NewestIndexedAt,
    int? EmbeddingsDimensions,
    string SkippedFilesNote);
