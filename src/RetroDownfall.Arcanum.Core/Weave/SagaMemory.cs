namespace RetroDownfall.Arcanum.Core.Weave;

/// <summary>
/// RAG Phase 4 — a single Saga memory retrieved via Divination for injection into the system prompt
/// (see <c>SystemPromptBuilder.Build</c>'s <c>sagaMemories</c> parameter). Intentionally slimmer than
/// <see cref="SagaMemoryDto"/> (no <c>Id</c>/<c>SessionId</c>/<c>Tags</c>/<c>Source</c>) — the model
/// only needs the memory's content, how relevant it is to the current prompt, and when it was formed.
/// </summary>
public sealed record SagaMemory(
    string Content,
    float Similarity,
    DateTimeOffset CreatedAt);
