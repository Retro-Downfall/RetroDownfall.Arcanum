-- One receipt per applied erasure, keyed by its mutation id, so a retried apply replays the recorded
-- outcome instead of erasing again. Both digests are keyed, and the request digest binds only
-- non-content fields. The counts, the external-evidence codes and the retained-copies mask describe
-- what was removed without naming it. There is no timestamp, so the table stays out of the UTC-instant
-- inventory and a receipt never records when a memory stopped existing.
--
-- Only the two scrub columns ever change, and memory_erasure_receipts_guard_update limits them to two
-- moves. ScrubPendingReasonMask holds one bit per pending reason: WalCheckpointPending = 1,
-- FullTextSecureDeleteUnverified = 2, VectorIndexScrubUnverified = 4.
CREATE TABLE IF NOT EXISTS memory_erasure_receipts (
    MutationId TEXT NOT NULL PRIMARY KEY CHECK (
        MutationId = upper(MutationId)
        AND length(MutationId) = 36
        AND substr(MutationId, 9, 1) = '-'
        AND substr(MutationId, 14, 1) = '-'
        AND substr(MutationId, 19, 1) = '-'
        AND substr(MutationId, 24, 1) = '-'),
    -- MemoryReviewStore: Covenant = 1, Saga = 2, Lexicon = 3.
    StoreCode INTEGER NOT NULL CHECK (StoreCode IN (1, 2, 3)),
    KeyId BLOB NOT NULL CHECK (length(KeyId) = 16),
    RequestDigest BLOB NOT NULL CHECK (length(RequestDigest) = 32),
    EffectDigest BLOB NOT NULL CHECK (length(EffectDigest) = 32),
    ErasedItemCount INTEGER NOT NULL CHECK (ErasedItemCount >= 1),
    RemovedRowCount INTEGER NOT NULL CHECK (RemovedRowCount >= 1),
    RemovedLabelCount INTEGER NOT NULL CHECK (RemovedLabelCount >= 0),
    RemovedRetirementSuppressionCount INTEGER NOT NULL CHECK (RemovedRetirementSuppressionCount >= 0),
    -- MemoryExternalEvidence: Known = 1, ReceiptWindow = 2, NotRecorded = 3, NotApplicable = 4.
    AuthorshipEvidenceCode INTEGER NOT NULL CHECK (AuthorshipEvidenceCode IN (1, 2, 3, 4)),
    ContextEvidenceCode INTEGER NOT NULL CHECK (ContextEvidenceCode IN (1, 2, 3, 4)),
    EmbeddingEvidenceCode INTEGER NOT NULL CHECK (EmbeddingEvidenceCode IN (1, 2, 3, 4)),
    BackupEvidenceCode INTEGER NOT NULL CHECK (BackupEvidenceCode IN (1, 2, 3, 4)),
    OtherExternalEvidenceCode INTEGER NOT NULL CHECK (OtherExternalEvidenceCode IN (1, 2, 3, 4)),
    RetainedCopiesMask INTEGER NOT NULL CHECK (RetainedCopiesMask >= 0),
    -- Pending = 1, Verified = 2.
    ScrubStateCode INTEGER NOT NULL CHECK (ScrubStateCode IN (1, 2)),
    ScrubPendingReasonMask INTEGER NOT NULL CHECK (ScrubPendingReasonMask BETWEEN 0 AND 7),
    -- A pending receipt always names why, and a verified one never does.
    CHECK ((ScrubStateCode = 1 AND ScrubPendingReasonMask <> 0) OR (ScrubStateCode = 2 AND ScrubPendingReasonMask = 0))
);

-- Status and key reset count receipts by store and by the key that wrote them.
CREATE INDEX IF NOT EXISTS idx_memory_erasure_receipts_store_key
    ON memory_erasure_receipts(StoreCode, KeyId);

-- Scrub reads only the receipts still pending.
CREATE INDEX IF NOT EXISTS idx_memory_erasure_receipts_pending
    ON memory_erasure_receipts(ScrubStateCode) WHERE ScrubStateCode = 1;
