namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Code-owned queue, indexing, and retrieval mechanics for versioned session attachments.
/// </summary>
public sealed record AttachmentEmbeddingSettings
{

    public int ChunkSizeCharacters { get; set; } = 1_000;

    public int ChunkOverlapCharacters { get; set; } = 100;

    public int MaxAttachmentsPerBatch { get; set; } = 8;

    public int QueueCapacity { get; set; } = 256;

    public int MaxRetrievedChunks { get; set; } = 5;

    public int MaxRetrievedAttachments { get; set; } = 4;

    public int MaxRetrievedBytes { get; set; } = 256 * 1024;

    public int MaxRetrievedTokens { get; set; } = 32 * 1024;

}
