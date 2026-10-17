CREATE TABLE IF NOT EXISTS long_rest_protected_receipt_evidence (
    ReceiptId TEXT NOT NULL PRIMARY KEY REFERENCES long_rest_receipts(ReceiptId) ON DELETE CASCADE,
    PartitionDigest BLOB NOT NULL CHECK (length(PartitionDigest) = 32),
    ProvenanceModeCode INTEGER NOT NULL CHECK (ProvenanceModeCode IN (1, 2)),
    ExactGenerationIds BLOB NULL,
    GenerationBloom BLOB NULL,
    WorkDigest BLOB NOT NULL CHECK (length(WorkDigest) = 32),
    EvidenceDigest BLOB NOT NULL CHECK (length(EvidenceDigest) = 32),
    CHECK ((ProvenanceModeCode = 1 AND ExactGenerationIds IS NOT NULL AND length(ExactGenerationIds) BETWEEN 16 AND 128 AND length(ExactGenerationIds) % 16 = 0 AND GenerationBloom IS NULL)
        OR (ProvenanceModeCode = 2 AND ExactGenerationIds IS NULL AND length(GenerationBloom) = 32 AND GenerationBloom <> zeroblob(32)))
);
