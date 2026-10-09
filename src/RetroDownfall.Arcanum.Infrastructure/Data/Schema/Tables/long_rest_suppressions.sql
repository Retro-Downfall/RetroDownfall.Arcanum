-- Suppression changes retrieval eligibility without changing a subject or any Annals version.
-- The exact source version is the unit: a successor or a later pin releases it naturally.
CREATE TABLE IF NOT EXISTS long_rest_suppressions (
    SourceVersionId TEXT NOT NULL PRIMARY KEY REFERENCES annal_versions(VersionId),
    SurvivorVersionId TEXT NOT NULL REFERENCES annal_versions(VersionId),
    ReceiptId TEXT NOT NULL REFERENCES long_rest_receipts(ReceiptId) ON DELETE CASCADE,
    CHECK (SourceVersionId <> SurvivorVersionId)
);

CREATE INDEX IF NOT EXISTS idx_long_rest_suppressions_survivor_version
ON long_rest_suppressions(SurvivorVersionId);

CREATE INDEX IF NOT EXISTS idx_long_rest_suppressions_receipt
ON long_rest_suppressions(ReceiptId);
