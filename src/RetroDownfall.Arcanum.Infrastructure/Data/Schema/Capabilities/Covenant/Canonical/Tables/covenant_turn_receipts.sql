CREATE TABLE IF NOT EXISTS covenant_turn_receipts (
    -- Session and Campaign are historical owner identities, not foreign keys: an optional tier must
    -- never put a trigger in core Session retention. Purging happens through the core
    -- owner-deletion journal instead.
    AssistantEntryId TEXT NOT NULL PRIMARY KEY,
    SessionId TEXT NOT NULL,
    CampaignId TEXT NULL,
    DatasetGeneration BLOB NOT NULL CHECK (length(DatasetGeneration) = 16),
    PlanDigest BLOB NOT NULL CHECK (length(PlanDigest) = 32),
    AttemptedAdmissionCount BLOB NOT NULL CHECK (length(AttemptedAdmissionCount) = 8),
    AttemptChainHead BLOB NOT NULL CHECK (length(AttemptChainHead) = 32),
    CommittedBranchDigest BLOB NOT NULL CHECK (length(CommittedBranchDigest) = 32),
    LineageHeadDigest BLOB NOT NULL CHECK (length(LineageHeadDigest) = 32),
    ExternalDisclosureCount BLOB NOT NULL CHECK (length(ExternalDisclosureCount) = 8),
    DisclosureChainHead BLOB NOT NULL CHECK (length(DisclosureChainHead) = 32),
    ConfirmedTokenCost INTEGER NOT NULL CHECK (ConfirmedTokenCost >= 0),
    ProposedTokenCost INTEGER NOT NULL CHECK (ProposedTokenCost >= 0),
    MutationCount INTEGER NOT NULL CHECK (MutationCount >= 0),
    FinalOutcomeCode INTEGER NOT NULL CHECK (FinalOutcomeCode IN (1, 2, 3, 4)),
    CreatedAtUtc TEXT NOT NULL
);

-- The per-Session tail is bounded at 1,024 rows and folded into covenant_turn_receipt_aggregate
-- from the oldest end, so the ordering index is the one the folding worker reads.
CREATE INDEX IF NOT EXISTS idx_covenant_turn_receipts_session_created
    ON covenant_turn_receipts(SessionId, CreatedAtUtc);

CREATE INDEX IF NOT EXISTS idx_covenant_turn_receipts_campaign
    ON covenant_turn_receipts(CampaignId);

CREATE INDEX IF NOT EXISTS idx_covenant_turn_receipts_created
    ON covenant_turn_receipts(CreatedAtUtc);
