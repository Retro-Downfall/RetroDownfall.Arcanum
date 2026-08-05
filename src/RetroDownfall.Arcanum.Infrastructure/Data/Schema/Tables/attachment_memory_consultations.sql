CREATE TABLE IF NOT EXISTS attachment_memory_consultations (
    SourceEntryId TEXT NOT NULL,
    SessionId TEXT NOT NULL,
    AttachmentId TEXT NOT NULL,
    LogicalKey TEXT NOT NULL,
    Version INTEGER NOT NULL,
    ContentHash TEXT NOT NULL,
    MaterializedAt TEXT NOT NULL,
    SourceType TEXT NOT NULL,
    PRIMARY KEY (SourceEntryId, AttachmentId, Version, MaterializedAt),
    FOREIGN KEY (SourceEntryId) REFERENCES Entries(Id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS IX_attachment_memory_consultations_Session_Time
ON attachment_memory_consultations(SessionId, MaterializedAt);

CREATE INDEX IF NOT EXISTS IX_attachment_memory_consultations_SourceEntry
ON attachment_memory_consultations(SourceEntryId);
