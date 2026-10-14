CREATE TABLE IF NOT EXISTS long_rest_discovery_nodes (
    NodeId TEXT NOT NULL PRIMARY KEY,
    GenerationId TEXT NOT NULL REFERENCES long_rest_discovery_generations(GenerationId) ON DELETE CASCADE,
    Layer INTEGER NOT NULL CHECK (Layer BETWEEN 0 AND 6),
    GroupOrdinal INTEGER NOT NULL CHECK (GroupOrdinal >= 0),
    MemoryId TEXT NULL,
    MembershipDigest BLOB NOT NULL CHECK (length(MembershipDigest) = 32),
    SourceDigest BLOB NOT NULL CHECK (length(SourceDigest) = 32),
    RecipeDigest BLOB NOT NULL CHECK (length(RecipeDigest) = 32),
    Summary TEXT NULL CHECK (Summary IS NULL OR length(CAST(Summary AS BLOB)) BETWEEN 1 AND 8192),
    SummaryDigest BLOB NULL CHECK (SummaryDigest IS NULL OR length(SummaryDigest) = 32),
    SensitivityCode INTEGER NOT NULL CHECK (SensitivityCode IN (0, 1)),
    ProvenanceJson TEXT NULL CHECK (ProvenanceJson IS NULL OR length(CAST(ProvenanceJson AS BLOB)) BETWEEN 2 AND 4096),
    PartitionDigest BLOB NULL CHECK (PartitionDigest IS NULL OR length(PartitionDigest) = 32),
    Revision INTEGER NOT NULL CHECK (typeof(Revision) = 'integer' AND Revision >= 0),
    UNIQUE (GenerationId, NodeId),
    UNIQUE (GenerationId, Layer, GroupOrdinal),
    FOREIGN KEY (GenerationId, MemoryId) REFERENCES long_rest_discovery_leaves(GenerationId, MemoryId) ON DELETE CASCADE,
    CHECK ((Layer = 0 AND MemoryId IS NOT NULL AND Summary IS NULL AND SummaryDigest IS NULL)
        OR (Layer > 0 AND MemoryId IS NULL AND Summary IS NOT NULL AND SummaryDigest IS NOT NULL)),
    CHECK ((SensitivityCode = 0 AND PartitionDigest IS NULL AND ProvenanceJson IS NULL)
        OR (SensitivityCode = 1 AND PartitionDigest IS NOT NULL AND ProvenanceJson IS NOT NULL))
);
