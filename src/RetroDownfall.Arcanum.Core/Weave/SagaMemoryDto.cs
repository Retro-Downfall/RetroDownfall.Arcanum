namespace RetroDownfall.Arcanum.Core.Weave;

using RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// RAG Phase 4 — a Saga memory as surfaced over <c>/api/saga</c> and <c>arcanum saga</c>. Mirrors the
/// <c>saga_memories</c> table (see <c>Infrastructure/Data/Schema/Tables/saga_memories.sql</c>) one-to-one.
/// </summary>
public sealed record SagaMemoryDto(
    string Id,
    string Content,
    DateTimeOffset CreatedAt,
    Guid? SessionId,
    string? Tags,
    string? Source,
    AttachmentMemoryProvenance? AttachmentProvenance = null);
