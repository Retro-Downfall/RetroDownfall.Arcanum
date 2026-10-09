-- One immutable, content-free disposition of an explicitly supplied exact-version manifest.
-- Applied and no-change decisions use the same receipt grammar; neither stores memory plaintext.
CREATE TABLE IF NOT EXISTS long_rest_receipts (
    ReceiptId TEXT NOT NULL PRIMARY KEY,
    PolicyVersion INTEGER NOT NULL CHECK (PolicyVersion = 1),
    KindCode INTEGER NOT NULL CHECK (KindCode BETWEEN 1 AND 3),
    OutcomeCode INTEGER NOT NULL CHECK (OutcomeCode IN (1, 2)),
    ReasonCode INTEGER NOT NULL CHECK (ReasonCode BETWEEN 0 AND 10),
    InputHash TEXT NOT NULL CHECK (length(InputHash) = 64),
    OutputHash TEXT NOT NULL CHECK (length(OutputHash) = 64),
    RequestedSurvivorVersionId TEXT NULL REFERENCES annal_versions(VersionId),
    SurvivorMemoryId TEXT NULL,
    SurvivorVersionId TEXT NULL REFERENCES annal_versions(VersionId),
    CreatedAtUtc TEXT NOT NULL,
    CHECK ((SurvivorMemoryId IS NULL) = (SurvivorVersionId IS NULL)),
    CHECK (OutcomeCode <> 1 OR SurvivorVersionId IS NOT NULL),
    CHECK ((OutcomeCode = 1 AND ReasonCode = 0) OR (OutcomeCode = 2 AND ReasonCode BETWEEN 1 AND 10))
);

CREATE INDEX IF NOT EXISTS idx_long_rest_receipts_survivor_version
ON long_rest_receipts(SurvivorVersionId);

CREATE INDEX IF NOT EXISTS idx_long_rest_receipts_requested_survivor_version
ON long_rest_receipts(RequestedSurvivorVersionId);
