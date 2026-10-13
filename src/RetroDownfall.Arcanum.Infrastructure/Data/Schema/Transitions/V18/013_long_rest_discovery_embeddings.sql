CREATE TABLE IF NOT EXISTS long_rest_discovery_embeddings (
    NodeId TEXT NOT NULL PRIMARY KEY REFERENCES long_rest_discovery_nodes(NodeId) ON DELETE CASCADE,
    Provider TEXT NOT NULL,
    Model TEXT NOT NULL,
    Dimensions INTEGER NOT NULL CHECK (Dimensions > 0),
    ValueDigest BLOB NOT NULL CHECK (length(ValueDigest) = 32),
    Vector BLOB NOT NULL CHECK (length(Vector) = Dimensions * 4),
    PaidEvidenceJson TEXT NOT NULL CHECK (length(CAST(PaidEvidenceJson AS BLOB)) BETWEEN 2 AND 32768)
);
