CREATE TABLE IF NOT EXISTS long_rest_protected_receipt_inputs (
    ReceiptId TEXT NOT NULL REFERENCES long_rest_protected_receipt_evidence(ReceiptId) ON DELETE CASCADE,
    Ordinal INTEGER NOT NULL CHECK (Ordinal BETWEEN 1 AND 16),
    MemoryId TEXT NOT NULL,
    ClaimId TEXT NOT NULL,
    VersionId TEXT NOT NULL,
    LabelId TEXT NOT NULL,
    LabelDigest BLOB NOT NULL CHECK (length(LabelDigest) = 32),
    SagaRevision INTEGER NOT NULL CHECK (typeof(SagaRevision) = 'integer' AND SagaRevision >= 0),
    SagaContentDigest BLOB NOT NULL CHECK (length(SagaContentDigest) = 32),
    SensitivityDigest BLOB NOT NULL CHECK (length(SensitivityDigest) = 32),
    LabelJson TEXT NOT NULL CHECK (length(CAST(LabelJson AS BLOB)) BETWEEN 2 AND 16384),
    PRIMARY KEY (ReceiptId, Ordinal),
    UNIQUE (ReceiptId, VersionId),
    FOREIGN KEY (ReceiptId, Ordinal) REFERENCES long_rest_receipt_inputs(ReceiptId, Ordinal) ON DELETE CASCADE
);
