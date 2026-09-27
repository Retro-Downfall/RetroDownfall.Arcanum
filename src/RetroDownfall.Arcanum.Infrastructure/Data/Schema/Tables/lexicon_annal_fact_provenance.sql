-- Historical source coordinates belong to an Annals version, independently of live content.
-- Attachment identity and its content hash locate the source; no fact or per-fact digest is retained.
CREATE TABLE IF NOT EXISTS lexicon_annal_fact_provenance (
    AnnalVersionId TEXT NOT NULL,
    FactOrdinal INTEGER NOT NULL CHECK (FactOrdinal >= 0),
    SessionId TEXT NOT NULL,
    AttachmentId TEXT NOT NULL,
    LogicalKey TEXT NOT NULL,
    AttachmentVersion INTEGER NOT NULL,
    AttachmentContentHash TEXT NOT NULL,
    MaterializedAt TEXT NOT NULL,
    SourceType TEXT NOT NULL,
    PRIMARY KEY (AnnalVersionId, FactOrdinal),
    FOREIGN KEY (AnnalVersionId) REFERENCES annal_versions(VersionId) ON DELETE CASCADE
);
