namespace RetroDownfall.Arcanum.Core.Weave;

/// <summary>
/// RAG Phase 4 — request body for <c>POST /api/saga/divine</c>: semantic search over Saga memories.
/// </summary>
/// <remarks>
/// <paramref name="SessionId"/> is how a caller says which turn's memory it is asking about. The scope
/// is then taken from that Session's immutable Campaign binding rather than from anything else in the
/// request, so naming a Session is a request to search as that Session and never a way to state an
/// authority it does not hold. A request that names none searches the installation-scoped memories,
/// once Campaign scoping is on.
/// </remarks>
public sealed record SagaSearchRequest(string Query, int? Limit = null, Guid? SessionId = null);
