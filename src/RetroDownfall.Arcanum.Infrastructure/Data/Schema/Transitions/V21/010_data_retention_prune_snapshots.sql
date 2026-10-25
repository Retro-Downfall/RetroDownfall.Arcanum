CREATE TABLE IF NOT EXISTS data_retention_prune_snapshots (
    OperationId TEXT NOT NULL REFERENCES LongRunningOperations(Id) ON DELETE CASCADE,
    Digest TEXT NOT NULL CHECK (length(Digest) = 64 AND Digest NOT GLOB '*[^0-9A-F]*'),
    Part INTEGER NOT NULL CHECK (Part >= 0),
    Payload BLOB NOT NULL CHECK (length(Payload) BETWEEN 1 AND 262144),
    PRIMARY KEY (OperationId, Digest, Part)
) WITHOUT ROWID;
