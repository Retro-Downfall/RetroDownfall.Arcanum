-- The durable answer already returned for one curation request identity. A replay resolves through
-- this row rather than running a second time, and a NoChange outcome is recorded exactly like an
-- applied one: without it, a replay of a deliberate no-op is indistinguishable from a request that
-- never arrived.
--
-- NOTE: this file is the head definition and its statements are copied character for character into
-- the version-2 transition files.
CREATE TABLE IF NOT EXISTS covenant_curation_receipts (
    MutationId TEXT NOT NULL PRIMARY KEY,
    RequestIdempotencyDigest BLOB NOT NULL CHECK (length(RequestIdempotencyDigest) = 32),
    AuthorizationDigest BLOB NOT NULL CHECK (length(AuthorizationDigest) = 32),
    FinalMutationDigest BLOB NOT NULL CHECK (length(FinalMutationDigest) = 32),
    CurationKindCode INTEGER NOT NULL CHECK (CurationKindCode IN (1, 2, 3, 4)),
    ScopeCode INTEGER NOT NULL CHECK (ScopeCode IN (1, 2)),
    CampaignId TEXT NULL,
    NormalizedKey TEXT NOT NULL CHECK (length(NormalizedKey) BETWEEN 1 AND 128),
    LaneCode INTEGER NOT NULL CHECK (LaneCode IN (1, 2)),
    KeyEpoch INTEGER NOT NULL CHECK (KeyEpoch >= 0),
    OutcomeCode INTEGER NOT NULL CHECK (OutcomeCode IN (1, 2)),
    ResultingVersionId TEXT NULL,
    ResultingRevision INTEGER NULL CHECK (ResultingRevision IS NULL OR ResultingRevision > 0),
    ResponseReceiptDigest BLOB NOT NULL CHECK (length(ResponseReceiptDigest) = 32),
    CommittedAtUtc TEXT NOT NULL,
    CHECK ((ScopeCode = 1 AND CampaignId IS NULL) OR (ScopeCode = 2 AND CampaignId IS NOT NULL)),
    -- An Applied change produced a version and a revision; a NoChange one produced neither and must
    -- not borrow the previous head's identity as if it had.
    CHECK (
        (OutcomeCode = 1 AND ResultingVersionId IS NOT NULL AND ResultingRevision IS NOT NULL)
        OR (OutcomeCode = 2 AND ResultingVersionId IS NULL AND ResultingRevision IS NULL)
    )
);
