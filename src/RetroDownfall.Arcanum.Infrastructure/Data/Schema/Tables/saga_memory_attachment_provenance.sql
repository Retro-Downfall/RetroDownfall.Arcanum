CREATE TABLE IF NOT EXISTS saga_memory_attachment_provenance (
    MemoryId TEXT PRIMARY KEY,
    SessionId TEXT NOT NULL,
    AttachmentId TEXT NOT NULL,
    LogicalKey TEXT NOT NULL,
    Version INTEGER NOT NULL,
    ContentHash TEXT NOT NULL,
    MaterializedAt TEXT NOT NULL,
    SourceType TEXT NOT NULL,
    FOREIGN KEY (MemoryId) REFERENCES saga_memories(Id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS IX_saga_memory_attachment_provenance_AttachmentId
ON saga_memory_attachment_provenance(AttachmentId);
