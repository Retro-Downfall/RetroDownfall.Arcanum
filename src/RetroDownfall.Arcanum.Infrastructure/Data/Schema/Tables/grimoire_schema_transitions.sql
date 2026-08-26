-- The always-present core journal for one tier's in-flight version run. A run is in flight exactly
-- while its row exists: the row is written in the same transaction as the first step's DDL and
-- deleted in the same transaction that records the finished version, so there is no phase column and
-- no other phase is observable.
--
-- The state of a run is (CompletedThroughVersion, BackfillName):
--   (c, NULL)  everything through version c is durably done; the DDL for c -> c+1 has not run.
--   (c, name)  the DDL for c -> c+1 committed and that step's sweep is draining at BackfillCursor.
--
-- A phase column would be a third statement of the same fact, and two measurements of one quantity
-- eventually disagree.
CREATE TABLE IF NOT EXISTS grimoire_schema_transitions (
    FamilyCode INTEGER NOT NULL,
    TransactionTierCode INTEGER NOT NULL,
    -- The version recorded in grimoire_feature_schemas when the run began. A run whose FromVersion no
    -- longer matches that row describes a database somebody else has since changed.
    FromVersion INTEGER NOT NULL CHECK (FromVersion > 0),
    TargetVersion INTEGER NOT NULL CHECK (TargetVersion > 0),
    CompletedThroughVersion INTEGER NOT NULL CHECK (CompletedThroughVersion > 0),
    -- What head looked like when the run began. A binary swapped mid-run cannot finish a run some
    -- other head defined.
    TargetSourceDefinitionFingerprint TEXT NOT NULL
        CHECK (length(TargetSourceDefinitionFingerprint) = 64),
    BackfillName TEXT NULL CHECK (BackfillName IS NULL OR length(BackfillName) BETWEEN 1 AND 64),
    BackfillCursor TEXT NULL CHECK (BackfillCursor IS NULL OR length(BackfillCursor) BETWEEN 1 AND 256),
    BackfillRowsProcessed INTEGER NOT NULL CHECK (BackfillRowsProcessed >= 0),
    Revision INTEGER NOT NULL CHECK (Revision >= 0),
    -- A bounded code, never an exception message: an unbounded error string in a core journal is both
    -- an unbounded row and a place for content to leak.
    LastDurableErrorCode TEXT NULL CHECK (
        LastDurableErrorCode IS NULL OR length(LastDurableErrorCode) BETWEEN 1 AND 64
    ),
    StartedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    PRIMARY KEY (FamilyCode, TransactionTierCode),
    CHECK (TargetVersion > FromVersion),
    -- A row saying the run is finished is a row that should not exist: finishing writes the metadata
    -- row and deletes this one in one transaction. Making "drained but not finalized" unrepresentable
    -- is what stops a later reader mistaking it for "still draining".
    CHECK (CompletedThroughVersion >= FromVersion AND CompletedThroughVersion < TargetVersion),
    CHECK (BackfillCursor IS NULL OR BackfillName IS NOT NULL)
);

-- Startup classification and every coordinator pass select by tier; the target lets a pass report how
-- far a run still has to go without resolving the chain.
CREATE INDEX IF NOT EXISTS idx_grimoire_schema_transitions_target
    ON grimoire_schema_transitions (TargetVersion, CompletedThroughVersion);
