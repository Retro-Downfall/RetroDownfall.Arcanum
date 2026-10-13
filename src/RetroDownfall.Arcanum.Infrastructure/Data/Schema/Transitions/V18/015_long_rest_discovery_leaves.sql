CREATE TABLE IF NOT EXISTS long_rest_discovery_leaves (
    GenerationId TEXT NOT NULL REFERENCES long_rest_discovery_generations(GenerationId) ON DELETE CASCADE,
    MemoryId TEXT NOT NULL,
    InsertionSequence INTEGER NOT NULL CHECK (InsertionSequence > 0),
    MutationEpoch INTEGER NOT NULL CHECK (MutationEpoch > 0),
    VersionId TEXT NOT NULL,
    SnapshotJson TEXT NOT NULL CHECK (length(CAST(SnapshotJson AS BLOB)) BETWEEN 2 AND 1048576),
    SourceDigest BLOB NOT NULL CHECK (length(SourceDigest) = 32),
    EmbeddingDigest BLOB NULL CHECK (EmbeddingDigest IS NULL OR length(EmbeddingDigest) = 32),
    EmbeddingDimensions INTEGER NULL CHECK (EmbeddingDimensions IS NULL OR EmbeddingDimensions > 0),
    LabelJson TEXT NULL CHECK (LabelJson IS NULL OR length(CAST(LabelJson AS BLOB)) BETWEEN 2 AND 16384),
    PRIMARY KEY (GenerationId, MemoryId),
    UNIQUE (GenerationId, VersionId),
    CHECK ((EmbeddingDigest IS NULL) = (EmbeddingDimensions IS NULL))
);

CREATE INDEX IF NOT EXISTS idx_long_rest_leaves_source ON long_rest_discovery_leaves(MemoryId, GenerationId);
CREATE INDEX IF NOT EXISTS idx_long_rest_leaves_version ON long_rest_discovery_leaves(VersionId, GenerationId);
