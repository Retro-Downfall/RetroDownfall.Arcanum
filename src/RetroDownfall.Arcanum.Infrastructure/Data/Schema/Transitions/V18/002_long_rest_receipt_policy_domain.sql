DROP TRIGGER IF EXISTS long_rest_receipts_guard_update;
DROP TRIGGER IF EXISTS long_rest_receipt_inputs_guard_update;
DROP TRIGGER IF EXISTS long_rest_suppressions_guard_update;
CREATE TEMP TABLE long_rest_suppressions_v18_copy AS SELECT * FROM long_rest_suppressions;
CREATE TEMP TABLE long_rest_receipt_inputs_v18_copy AS SELECT * FROM long_rest_receipt_inputs;
CREATE TEMP TABLE long_rest_receipts_v18_copy AS SELECT * FROM long_rest_receipts;
DROP TABLE long_rest_suppressions;
DROP TABLE long_rest_receipt_inputs;
DROP TABLE long_rest_receipts;
-- One immutable, content-free disposition of an explicitly supplied exact-version manifest.
-- Applied and no-change decisions use the same receipt grammar; neither stores memory plaintext.
CREATE TABLE IF NOT EXISTS long_rest_receipts (
    ReceiptId TEXT NOT NULL PRIMARY KEY,
    PolicyVersion INTEGER NOT NULL CHECK (PolicyVersion IN (1, 2)),
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

INSERT INTO long_rest_receipts SELECT * FROM long_rest_receipts_v18_copy;
DROP TABLE long_rest_receipts_v18_copy;
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

INSERT INTO long_rest_receipt_inputs SELECT * FROM long_rest_receipt_inputs_v18_copy;
DROP TABLE long_rest_receipt_inputs_v18_copy;
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

INSERT INTO long_rest_suppressions SELECT * FROM long_rest_suppressions_v18_copy;
DROP TABLE long_rest_suppressions_v18_copy;
CREATE TRIGGER IF NOT EXISTS long_rest_receipts_guard_update
BEFORE UPDATE ON long_rest_receipts
BEGIN
    SELECT RAISE(ABORT, 'long_rest_receipts is append-only; transformations are immutable.');
END;

CREATE TRIGGER IF NOT EXISTS long_rest_receipt_inputs_guard_update
BEFORE UPDATE ON long_rest_receipt_inputs
BEGIN
    SELECT RAISE(ABORT, 'long_rest_receipt_inputs is append-only; transformations are immutable.');
END;

CREATE TRIGGER IF NOT EXISTS long_rest_suppressions_guard_update
BEFORE UPDATE ON long_rest_suppressions
BEGIN
    SELECT RAISE(ABORT, 'long_rest_suppressions is append-only; transformations are immutable.');
END;
