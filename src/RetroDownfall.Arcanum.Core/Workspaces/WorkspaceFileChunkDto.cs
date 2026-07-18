namespace RetroDownfall.Arcanum.Core.Workspaces;

/// <summary>
/// Phase 7 (RAG / The Weave inspector) — one indexed chunk of a workspace file, returned by
/// <c>GET /api/workspaces/{id}/files/chunks</c>. <see cref="ContentPreview"/> is hard-capped to a
/// character budget (UTF-8/surrogate-safe); the full chunk text is never returned by this route.
/// <see cref="TotalChunksForFile"/> is the chunk count for the same <see cref="RelativePath"/>, mirroring
/// <see cref="WorkspaceSearchResult.TotalChunks"/>.
/// </summary>
public sealed record WorkspaceFileChunkDto(
    string ChunkId,
    string RelativePath,
    int ChunkIndex,
    int TotalChunksForFile,
    string ContentPreview,
    int CharOffset,
    int CharLength,
    DateTimeOffset IndexedAt,
    DateTimeOffset FileLastWriteTime);
