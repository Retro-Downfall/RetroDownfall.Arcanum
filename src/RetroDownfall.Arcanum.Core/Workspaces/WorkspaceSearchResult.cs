namespace RetroDownfall.Arcanum.Core.Workspaces;

/// <summary>
/// RAG Phase 3 — a single Divination hit against a workspace's indexed files, joined with its chunk
/// metadata for display. Returned by <c>POST /api/workspaces/{id}/files/divine</c>.
/// </summary>
public sealed record WorkspaceSearchResult(
    string RelativePath,
    int ChunkIndex,
    int TotalChunks,
    float Similarity,
    string ContentPreview);
