CREATE TABLE IF NOT EXISTS lexicon_fact_attachment_provenance (
    EntryId TEXT NOT NULL,
    FactHash TEXT NOT NULL,
    Fact TEXT NOT NULL,
    SessionId TEXT NOT NULL,
    AttachmentId TEXT NOT NULL,
    LogicalKey TEXT NOT NULL,
    Version INTEGER NOT NULL,
    ContentHash TEXT NOT NULL,
    MaterializedAt TEXT NOT NULL,
    SourceType TEXT NOT NULL,
    PRIMARY KEY (EntryId, FactHash),
    FOREIGN KEY (EntryId) REFERENCES lexicon_entries(Id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS IX_lexicon_fact_attachment_provenance_AttachmentId
ON lexicon_fact_attachment_provenance(AttachmentId);
