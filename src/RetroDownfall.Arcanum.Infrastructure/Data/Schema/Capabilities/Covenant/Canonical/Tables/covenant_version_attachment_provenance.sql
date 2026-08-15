CREATE TABLE IF NOT EXISTS covenant_version_attachment_provenance (
    VersionId TEXT NOT NULL REFERENCES covenant_versions(VersionId),
    Ordinal INTEGER NOT NULL CHECK (Ordinal >= 0),
    -- Attachment, Session, and Entry identifiers are historical, not foreign keys. Deleting a source
    -- attachment must not erase durable provenance, matching the Saga and Lexicon rule; availability
    -- is resolved dynamically at read time instead.
    AttachmentId TEXT NOT NULL,
    AttachmentVersionIdentity TEXT NOT NULL,
    LogicalKey TEXT NOT NULL,
    ContentHash BLOB NOT NULL CHECK (length(ContentHash) = 32),
    SourceRangeKindCode INTEGER NOT NULL CHECK (SourceRangeKindCode IN (1, 2, 3)),
    SourceStart INTEGER NULL CHECK (SourceStart IS NULL OR SourceStart >= 0),
    SourceEnd INTEGER NULL CHECK (SourceEnd IS NULL OR SourceEnd >= 0),
    SourceTurnId TEXT NULL,
    MaterializationReference TEXT NULL,
    PRIMARY KEY (VersionId, Ordinal),
    -- WholeSource carries no offsets; a Utf16Range or ByteRange carries both, ordered. These are the
    -- immutable source coordinates, deliberately separate from provider-payload occurrence offsets.
    CHECK (
        (SourceRangeKindCode = 1 AND SourceStart IS NULL AND SourceEnd IS NULL)
        OR (SourceRangeKindCode IN (2, 3) AND SourceStart IS NOT NULL AND SourceEnd IS NOT NULL AND SourceEnd >= SourceStart)
    )
);

CREATE INDEX IF NOT EXISTS idx_covenant_version_attachment_provenance_attachment
    ON covenant_version_attachment_provenance(AttachmentId, AttachmentVersionIdentity);
