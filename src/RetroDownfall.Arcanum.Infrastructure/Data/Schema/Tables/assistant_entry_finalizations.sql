-- The durable one-shot guard for every assistant placeholder. Absence means pending and presence
-- means terminal, so a retry resolves through the stored outcome instead of running a second turn.
-- A successful empty response is an ordinary Committed row: empty content is never a sentinel here,
-- and treating it as one would let a valid empty answer be mistaken for an unfinished turn.
--
-- The key is the historical assistant Entry ID with no Entry foreign key, because a Discarded
-- placeholder is deleted while its guard has to survive for replay. The Session reference is a real
-- cascade: the guard is Session-owned and leaves with it.
CREATE TABLE IF NOT EXISTS assistant_entry_finalizations (
    AssistantEntryId TEXT NOT NULL PRIMARY KEY,
    SessionId TEXT NOT NULL REFERENCES "Sessions" ("Id") ON DELETE CASCADE,
    OutcomeCode INTEGER NOT NULL CHECK (OutcomeCode IN (1, 2, 3, 4)),
    ContentSensitivityCode INTEGER NOT NULL CHECK (ContentSensitivityCode IN (0, 1)),
    ContentSensitivityDigest BLOB NOT NULL CHECK (length(ContentSensitivityDigest) = 32),
    RequestDigest BLOB NOT NULL CHECK (length(RequestDigest) = 32),
    FinalReceiptDigest BLOB NULL CHECK (FinalReceiptDigest IS NULL OR length(FinalReceiptDigest) = 32),
    SourceEvidenceDigest BLOB NULL CHECK (SourceEvidenceDigest IS NULL OR length(SourceEvidenceDigest) = 32),
    FinalizedAtUtc TEXT NOT NULL,
    -- CommittedImported and CommittedForked are produced by an atomic copy transaction and are
    -- explicitly non-replayable, so each one names the evidence it was copied from. A native
    -- Committed or Discarded row has no source and must not borrow one, because a source digest is
    -- what marks a row as non-replayable.
    CHECK (
        (OutcomeCode IN (3, 4) AND SourceEvidenceDigest IS NOT NULL)
        OR (OutcomeCode IN (1, 2) AND SourceEvidenceDigest IS NULL)
    )
);

CREATE INDEX IF NOT EXISTS idx_assistant_entry_finalizations_session_outcome
    ON assistant_entry_finalizations(SessionId, OutcomeCode);

CREATE INDEX IF NOT EXISTS idx_assistant_entry_finalizations_session_finalized
    ON assistant_entry_finalizations(SessionId, FinalizedAtUtc);
