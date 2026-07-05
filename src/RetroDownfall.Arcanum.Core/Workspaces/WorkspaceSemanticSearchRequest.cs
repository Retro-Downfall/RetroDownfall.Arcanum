namespace RetroDownfall.Arcanum.Core.Workspaces;

/// <summary>
/// RAG Phase 3 — request body for <c>POST /api/workspaces/{id}/files/divine</c> (semantic codebase
/// retrieval).
/// </summary>
public sealed record WorkspaceSemanticSearchRequest(string Query, int? Limit = null);
