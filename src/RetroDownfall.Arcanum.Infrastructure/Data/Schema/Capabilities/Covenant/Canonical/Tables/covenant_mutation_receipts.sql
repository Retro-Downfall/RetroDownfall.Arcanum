-- EntryId names the entry a receipt resolved, for an Applied and a NoChange outcome alike, so an entry
-- erasure can find every receipt for its entry by that column. It is null only on receipts written
-- before version 6. It sits where SQLite's ALTER TABLE ... ADD COLUMN splices it into the stored
-- declaration, so a fresh installation and one the version-6 step evolved store the same text.
CREATE TABLE IF NOT EXISTS covenant_mutation_receipts (
    MutationId TEXT NOT NULL PRIMARY KEY,
    RequestIdempotencyDigest BLOB NOT NULL CHECK (length(RequestIdempotencyDigest) = 32),
    AuthorizationDigest BLOB NOT NULL CHECK (length(AuthorizationDigest) = 32),
    FinalMutationDigest BLOB NOT NULL CHECK (length(FinalMutationDigest) = 32),
    MutationKindCode INTEGER NOT NULL CHECK (MutationKindCode IN (1, 2, 3, 4)),
    ScopeCode INTEGER NOT NULL CHECK (ScopeCode IN (1, 2)),
    CampaignId TEXT NULL,
    TargetIdentityDigest BLOB NOT NULL CHECK (length(TargetIdentityDigest) = 32),
    LaneCode INTEGER NOT NULL CHECK (LaneCode IN (1, 2)),
    OutcomeCode INTEGER NOT NULL CHECK (OutcomeCode IN (1, 2)),
    ResultingVersionId TEXT NULL,
    ResultingLaneRevision INTEGER NULL CHECK (ResultingLaneRevision IS NULL OR ResultingLaneRevision > 0),
    ResponseReceiptDigest BLOB NOT NULL CHECK (length(ResponseReceiptDigest) = 32),
    SourceTurnId TEXT NULL,
    CommittedAtUtc TEXT NOT NULL, EntryId TEXT NULL,
    CHECK ((ScopeCode = 1 AND CampaignId IS NULL) OR (ScopeCode = 2 AND CampaignId IS NOT NULL)),
    -- An Applied mutation produced a version and revision; a NoChange one produced neither and must
    -- not borrow the previous head's identity as if it had.
    CHECK (
        (OutcomeCode = 1 AND ResultingVersionId IS NOT NULL AND ResultingLaneRevision IS NOT NULL)
        OR (OutcomeCode = 2 AND ResultingVersionId IS NULL AND ResultingLaneRevision IS NULL)
    )
);

CREATE INDEX IF NOT EXISTS idx_covenant_mutation_receipts_scope_quota
    ON covenant_mutation_receipts(ScopeCode, CampaignId, CommittedAtUtc);

CREATE INDEX IF NOT EXISTS idx_covenant_mutation_receipts_source_turn
    ON covenant_mutation_receipts(SourceTurnId);

CREATE INDEX IF NOT EXISTS idx_covenant_mutation_receipts_resulting_version
    ON covenant_mutation_receipts(ResultingVersionId);

-- An entry erasure finds every receipt for its entry through this index.
CREATE INDEX IF NOT EXISTS idx_covenant_mutation_receipts_entry
    ON covenant_mutation_receipts(EntryId);
