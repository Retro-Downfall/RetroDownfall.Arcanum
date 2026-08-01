namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Bounded extraction, queue, indexing, and retrieval mechanics for versioned session attachments.
/// </summary>
public sealed record AttachmentEmbeddingSettings
{

    public int MaxAttachmentBytes { get; set; } = 2 * 1024 * 1024;

    public int MaxExtractedCharacters { get; set; } = 200_000;

    public int ChunkSizeCharacters { get; set; } = 1_000;

    public int ChunkOverlapCharacters { get; set; } = 100;

    public int MaxChunksPerAttachment { get; set; } = 256;

    public int MaxAttachmentsPerBatch { get; set; } = 8;

    public int QueueCapacity { get; set; } = 256;

    public int MaxRetries { get; set; } = 3;

    public int RetryDelaySeconds { get; set; } = 5;

    public int ProcessingTimeoutSeconds { get; set; } = 60;

    public int MaxRetrievedChunks { get; set; } = 5;

    public int MaxRetrievedAttachments { get; set; } = 4;

    public int MaxRetrievedBytes { get; set; } = 256 * 1024;

    public int MaxRetrievedTokens { get; set; } = 32 * 1024;

}
