namespace RetroDownfall.Arcanum.Core.Tower;

/// <summary>
/// RAG Phase 2 — request body for <c>POST /api/sessions/divine</c> (session semantic search).
/// </summary>
public sealed record SemanticSearchRequest(
    string Query,
    Guid? CampaignId = null,
    string? Status = null,
    int? Limit = null);

/// <summary>
/// RAG Phase 2 — a single Divination hit against The Weave, joined with its owning session and entry
/// metadata for display.
/// </summary>
public sealed record SemanticSessionSearchResult(
    Guid SessionId,
    string? SessionTitle,
    Guid EntryId,
    string EntryRole,
    string EntryContentPreview,
    float Similarity,
    DateTimeOffset EntryCreatedAt);

/// <summary>
/// RAG Phase 2 — response envelope for <c>POST /api/sessions/divine</c>.
/// </summary>
public sealed record SemanticSearchResult(
    SemanticSessionSearchResult[] Results,
    bool HasMore,
    string? NextCursor);
