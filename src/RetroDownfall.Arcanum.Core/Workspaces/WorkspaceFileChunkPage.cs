namespace RetroDownfall.Arcanum.Core.Workspaces;

/// <summary>
/// Phase 7 (RAG / The Weave inspector) — a bounded, paginated page of <see cref="WorkspaceFileChunkDto"/>
/// for one workspace, returned by <c>GET /api/workspaces/{id}/files/chunks</c>. <see cref="Total"/> is the
/// full row count matching the (optional) <see cref="RelativePathFilter"/>; <see cref="HasMore"/> is true
/// when another page follows at <c>offset = Offset + Limit</c>.
/// </summary>
public sealed record WorkspaceFileChunkPage(
    WorkspaceFileChunkDto[] Chunks,
    int Total,
    int Limit,
    int Offset,
    bool HasMore,
    string? RelativePathFilter);
