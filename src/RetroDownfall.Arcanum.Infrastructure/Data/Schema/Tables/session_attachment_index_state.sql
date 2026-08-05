CREATE TABLE IF NOT EXISTS session_attachment_index_state (
    AttachmentId TEXT PRIMARY KEY,
    Status TEXT NOT NULL,
    ContentSha256 TEXT NOT NULL,
    AttemptCount INTEGER NOT NULL DEFAULT 0,
    FailureReason TEXT,
    ExtractedAt TEXT,
    IndexedAt TEXT,
    PublishedGenerationId TEXT,
    PendingGenerationId TEXT,
    NextChunkIndex INTEGER NOT NULL DEFAULT 0,
    PendingEmbeddingDimension INTEGER,
    PendingPipelineFingerprint TEXT,
    PendingExtractedAt TEXT,
    UpdatedAt TEXT NOT NULL,
    FOREIGN KEY(AttachmentId) REFERENCES SessionAttachments(Id) ON DELETE CASCADE
);
