CREATE TABLE IF NOT EXISTS campaign_path_operation_receipts (
    -- The content-free replay ledger for the single-Campaign PathMutation arm only. It is keyed by
    -- the owner operation so a retry reaches the recorded answer; the request digest beside it is
    -- compared rather than trusted, because the same ID carrying a different digest is a different
    -- request and must conflict instead of inheriting this result.
    OwnerOperationId TEXT NOT NULL PRIMARY KEY CHECK (length(OwnerOperationId) > 0),
    -- Historical, and deliberately without a foreign key: a retry after owner deletion has to be
    -- answerable, and the answer is Campaign not found rather than repeated filesystem work.
    CampaignId TEXT NOT NULL,
    -- Register = 1, Update = 2, RepairMoved = 3, Deregister = 4, TakeoverOrphan = 5.
    OperationCode INTEGER NOT NULL CHECK (OperationCode IN (1, 2, 3, 4, 5)),
    ApplyRequestDigest BLOB NOT NULL CHECK (length(ApplyRequestDigest) = 32),
    EffectDigest BLOB NOT NULL CHECK (length(EffectDigest) = 32),
    -- Active = 1, LegacyUnresolved = 2, Missing = 3, Invalid = 4, OrphanCleanupPending = 5,
    -- OperationPending = 6.
    ResultStateCode INTEGER NOT NULL CHECK (ResultStateCode IN (1, 2, 3, 4, 5, 6)),
    -- None = 0, Register = 1, RepairMoved = 2, RetryPendingOperation = 3, ReviewOrphan = 4. Stored
    -- beside the state so a replay returns the exact remediation the operator was first shown.
    RemediationCode INTEGER NOT NULL CHECK (RemediationCode IN (0, 1, 2, 3, 4)),
    -- Null for an operation that left no identity behind, deregistration being the ordinary case. A
    -- receipt must not borrow the previous revision and appear to have produced one.
    ResultingRevision INTEGER NULL CHECK (ResultingRevision IS NULL OR ResultingRevision > 0),
    CompletedAtUtc TEXT NOT NULL
);

-- Replay lookup and the per-Campaign retention bound both read this ledger one Campaign at a time,
-- and the capacity check runs before any filesystem work, so it cannot afford a table scan.
CREATE INDEX IF NOT EXISTS idx_campaign_path_operation_receipts_campaign_replay
    ON campaign_path_operation_receipts(CampaignId, CompletedAtUtc);
