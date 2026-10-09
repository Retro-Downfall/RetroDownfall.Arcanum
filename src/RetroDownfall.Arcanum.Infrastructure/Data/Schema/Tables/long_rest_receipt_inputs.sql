-- Exact source coordinates and content bindings, not copies of the observations.
CREATE TABLE IF NOT EXISTS long_rest_receipt_inputs (
    ReceiptId TEXT NOT NULL REFERENCES long_rest_receipts(ReceiptId) ON DELETE CASCADE,
    Ordinal INTEGER NOT NULL CHECK (Ordinal BETWEEN 1 AND 16),
    MemoryId TEXT NOT NULL,
    VersionId TEXT NOT NULL REFERENCES annal_versions(VersionId),
    ContentHashFormatCode INTEGER NOT NULL CHECK (ContentHashFormatCode IN (1, 2)),
    ContentHash BLOB NOT NULL CHECK (length(ContentHash) = 32),
    SnapshotHash TEXT NOT NULL CHECK (length(SnapshotHash) = 64),
    SnapshotJson TEXT NOT NULL CHECK (length(SnapshotJson) BETWEEN 2 AND 1048576),
    PRIMARY KEY (ReceiptId, Ordinal),
    UNIQUE (ReceiptId, VersionId)
);

CREATE INDEX IF NOT EXISTS idx_long_rest_receipt_inputs_version
ON long_rest_receipt_inputs(VersionId, ReceiptId);
