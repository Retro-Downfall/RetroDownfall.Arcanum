-- The immutable tombstone left when a committed sensitive assistant artifact is purged while its
-- Session is retained. Integrity for a committed assistant entry is exactly one of two shapes: a
-- live artifact with its live label, or this receipt with both absent. Without the receipt the
-- purged entry would be indistinguishable from one that never existed, and a retry of its turn
-- claim would recreate or return the response the purge was supposed to remove. The finalization
-- guard and the turn claim survive alongside it, so the retry answers Covenant.ArtifactErased.
CREATE TABLE IF NOT EXISTS assistant_entry_erasure_receipts (
    AssistantEntryId TEXT NOT NULL PRIMARY KEY,
    SessionId TEXT NOT NULL REFERENCES "Sessions" ("Id") ON DELETE CASCADE,
    -- The digest of the finalization guard this receipt replaces content for. It binds the receipt
    -- to one exact terminal outcome, so a later guard for a different turn cannot inherit it.
    FinalizationGuardDigest BLOB NOT NULL CHECK (length(FinalizationGuardDigest) = 32),
    -- The two purge paths that erase content while keeping the Session: EntryRetention = 1 and
    -- CovenantReset = 2. Whole-Session retention deletes the receipt instead of writing one.
    ErasureReasonCode INTEGER NOT NULL CHECK (ErasureReasonCode IN (1, 2)),
    OperationId TEXT NOT NULL,
    ErasedAtUtc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_assistant_entry_erasure_receipts_session
    ON assistant_entry_erasure_receipts(SessionId, ErasedAtUtc);

CREATE INDEX IF NOT EXISTS idx_assistant_entry_erasure_receipts_operation
    ON assistant_entry_erasure_receipts(OperationId);
