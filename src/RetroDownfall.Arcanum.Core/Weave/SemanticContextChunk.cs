namespace RetroDownfall.Arcanum.Core.Weave;

/// <summary>
/// RAG Phase 3 — a single retrieved codebase chunk, ready for injection into the system prompt by
/// <c>SystemPromptBuilder.Build</c>. Produced by <c>WizardIntelligenceProvider</c>'s semantic context
/// retrieval step (workspace file Divination joined against <c>workspace_file_chunks</c> metadata).
/// </summary>
public sealed record SemanticContextChunk(
    string RelativePath,
    int ChunkIndex,
    int TotalChunks,
    float Similarity,
    string Content);
