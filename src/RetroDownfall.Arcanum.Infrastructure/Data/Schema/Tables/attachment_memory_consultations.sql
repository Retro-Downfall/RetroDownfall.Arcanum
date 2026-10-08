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

-- Retention compares SessionId normalized - lower(replace(SessionId, '-', '')) - when a Session is
-- deleted, both in the delete itself and in the post-commit count that proves it, and SQLite cannot
-- answer a function-wrapped column from an ordinary column index. The expression here has to stay
-- character for character the shape the predicate has, because that is how SQLite decides the index
-- applies.
CREATE INDEX IF NOT EXISTS IX_attachment_memory_consultations_SessionId_Norm
ON attachment_memory_consultations(lower(replace(SessionId, '-', '')));
