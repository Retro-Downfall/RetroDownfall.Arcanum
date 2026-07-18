using RetroDownfall.Arcanum.Core.Workspaces;

namespace RetroDownfall.Arcanum.Core.Weave;

/// <summary>
/// Phase 7 (RAG / The Weave inspector) — read-only inspection of a workspace's indexed chunks. Queries
/// the existing <c>workspace_file_chunks</c> / <c>workspace_file_embeddings</c> tables directly; never
/// triggers indexing and never mutates state. Scoped because it depends on the scoped
/// <c>ArcanumDbContext</c>.
/// </summary>
public interface IWorkspaceIndexInspectorService
{

    /// <summary>
    /// Returns the indexing status (file/chunk counts, oldest/newest <c>IndexedAt</c>, stored embedding
    /// dimensions) for <paramref name="workspace"/>. <paramref name="vectorMode"/>,
    /// <paramref name="vectorDiagnostic"/>, and <paramref name="indexingEnabled"/> are resolved by the
    /// caller from <c>/api/meta</c>-equivalent state and merged into the returned DTO.
    /// </summary>
    Task<WorkspaceIndexStatusDto> GetStatusAsync(
        WorkspaceInfo workspace,
        string vectorMode,
        string vectorDiagnostic,
        bool indexingEnabled,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns a bounded page of chunk previews for <paramref name="workspace"/>, optionally filtered to a
    /// single <paramref name="relativePath"/> (already validated/canonicalized by the caller). Content is
    /// preview-capped; the full chunk text is never returned.
    /// </summary>
    Task<WorkspaceFileChunkPage> GetChunksAsync(
        WorkspaceInfo workspace,
        string? relativePath,
        int limit,
        int offset,
        CancellationToken cancellationToken);

}
