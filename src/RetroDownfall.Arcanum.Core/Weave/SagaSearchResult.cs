namespace RetroDownfall.Arcanum.Core.Weave;

/// <summary>
/// RAG Phase 4 — response payload for <c>POST /api/saga/divine</c>. <see cref="Similarities"/> is
/// parallel to <see cref="Memories"/> (same index refers to the same memory).
/// </summary>
public sealed record SagaSearchResult(SagaMemoryDto[] Memories, float[] Similarities);
