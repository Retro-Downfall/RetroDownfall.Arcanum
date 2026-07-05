namespace RetroDownfall.Arcanum.Core.Weave;

/// <summary>
/// RAG Phase 4 — aggregate summary of Saga memory storage, surfaced via <c>GET /api/saga/stats</c>.
/// </summary>
public sealed record SagaStats(
    int TotalCount,
    int SessionCount,
    DateTimeOffset? OldestCreatedAt,
    DateTimeOffset? NewestCreatedAt);
