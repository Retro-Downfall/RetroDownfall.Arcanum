CREATE TABLE IF NOT EXISTS session_campaign_binding_resolution_receipts (
    -- The durable answer already returned for one operation. A replay is answered from this row, so
    -- the operation ID is the key and the request digest is compared rather than trusted: the same
    -- ID with a different digest is a different request and must conflict instead of returning a
    -- result it never asked for.
    OperationId TEXT NOT NULL PRIMARY KEY CHECK (length(OperationId) > 0),
    ApplyRequestDigest BLOB NOT NULL CHECK (length(ApplyRequestDigest) = 32),
    SessionId TEXT NOT NULL,
    -- Resolution produces a final binding, so the unresolved legacy kind is not a legal result. A
    -- receipt recording kind 3 would claim the one-time transition happened while leaving the
    -- Session exactly as blocked as before.
    FinalBindingKindCode INTEGER NOT NULL CHECK (FinalBindingKindCode IN (1, 2)),
    CampaignId TEXT NULL,
    -- The exact prior binding row this transition consumed. It is what proves the receipt describes
    -- the row that was actually replaced, so a stale plan cannot authorize a newer binding.
    PriorBindingRowDigest BLOB NOT NULL CHECK (length(PriorBindingRowDigest) = 32),
    AuthorityEpoch INTEGER NOT NULL CHECK (AuthorityEpoch > 0),
    -- Applied = 1, NoChange = 2, matching the mutation outcome vocabulary the Covenant receipts
    -- already use, so one replay answer means the same thing everywhere.
    ResultCode INTEGER NOT NULL CHECK (ResultCode IN (1, 2)),
    ResolvedAtUtc TEXT NOT NULL,
    CHECK (
        (FinalBindingKindCode = 1 AND CampaignId IS NULL)
        OR (FinalBindingKindCode = 2 AND CampaignId IS NOT NULL)
    )
);

-- One Session resolves once. The uniqueness is the enforcement of that, not an optimization: a
-- second receipt for the same Session would mean the one-time transition ran twice.
CREATE UNIQUE INDEX IF NOT EXISTS ux_session_campaign_binding_resolution_receipts_session
    ON session_campaign_binding_resolution_receipts(SessionId);
