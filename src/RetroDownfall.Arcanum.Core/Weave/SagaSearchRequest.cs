namespace RetroDownfall.Arcanum.Core.Weave;

/// <summary>
/// RAG Phase 4 — request body for <c>POST /api/saga/divine</c>: semantic search over Saga memories.
/// </summary>
public sealed record SagaSearchRequest(string Query, int? Limit = null);
