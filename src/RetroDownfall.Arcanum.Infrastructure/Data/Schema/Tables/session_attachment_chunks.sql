CREATE TABLE IF NOT EXISTS session_attachment_chunks (
    ChunkId TEXT PRIMARY KEY,
    GenerationId TEXT NOT NULL DEFAULT 'legacy',
    SessionId TEXT NOT NULL,
    AttachmentId TEXT NOT NULL,
    LogicalKey TEXT NOT NULL,
    Version INTEGER NOT NULL,
    OriginalFileName TEXT NOT NULL,
    MimeType TEXT NOT NULL,
    ContentSha256 TEXT NOT NULL,
    ChunkIndex INTEGER NOT NULL,
    CharacterStart INTEGER NOT NULL,
    CharacterEnd INTEGER NOT NULL,
    StartLine INTEGER NOT NULL,
    EndLine INTEGER NOT NULL,
    Content TEXT NOT NULL,
    EmbeddingDimension INTEGER NOT NULL,
    ExtractedAt TEXT NOT NULL,
    IndexedAt TEXT NOT NULL,
    RetrievalScope TEXT,
    UNIQUE(AttachmentId, GenerationId, ChunkIndex),
    FOREIGN KEY(AttachmentId) REFERENCES SessionAttachments(Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_session_attachment_chunks_session
    ON session_attachment_chunks(SessionId, RetrievalScope);
CREATE INDEX IF NOT EXISTS idx_session_attachment_chunks_attachment
    ON session_attachment_chunks(AttachmentId);

-- Snapshot pages seek within one scope in source-id order.
CREATE INDEX IF NOT EXISTS idx_session_attachment_chunks_tapestry_scope_id ON session_attachment_chunks(SessionId, ChunkId COLLATE BINARY);

CREATE INDEX IF NOT EXISTS idx_memory_search_session_attachment_chunks_order ON session_attachment_chunks(IndexedAt DESC,ChunkIndex ASC,ChunkId ASC);

CREATE INDEX IF NOT EXISTS idx_memory_search_session_attachment_chunks_owner ON session_attachment_chunks(SessionId,IndexedAt DESC,ChunkIndex ASC,ChunkId ASC);
