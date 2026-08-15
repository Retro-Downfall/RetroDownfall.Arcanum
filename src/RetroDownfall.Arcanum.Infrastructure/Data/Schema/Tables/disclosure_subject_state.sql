-- One row per disclosure subject: a logical turn, or a durable operation such as an encrypted
-- backup. It owns ordinal allocation, the overall counts, and the rolling chain, so exactly one
-- writer advances them and a receipt can never be counted twice or skipped. Compaction folds detail
-- into aggregates but is forbidden from touching anything here except the folded watermark.
CREATE TABLE IF NOT EXISTS disclosure_subject_state (
    OriginInstallationId TEXT NOT NULL CHECK (length(OriginInstallationId) > 0),
    -- CovenantDisclosureSubjectKind: Turn = 1, Operation = 2.
    SubjectKind INTEGER NOT NULL CHECK (SubjectKind IN (1, 2)),
    SubjectId TEXT NOT NULL CHECK (length(SubjectId) > 0),
    -- Open = 1, Orphaned = 2, Completed = 3, Abandoned = 4. Closed because an unknown lifecycle
    -- value would let a subject dispatch after it was supposed to be sealed.
    LifecycleCode INTEGER NOT NULL CHECK (LifecycleCode IN (1, 2, 3, 4)),
    -- Which boot created the subject. A prior-boot Open subject is not this process's to resume, so
    -- startup can tell an adoptable orphan from a live turn without guessing.
    CreatorBootId TEXT NOT NULL CHECK (length(CreatorBootId) > 0),
    LastHeartbeatAtUtc TEXT NOT NULL,
    ClosedAtUtc TEXT NULL,
    ProviderAttemptCount INTEGER NOT NULL CHECK (ProviderAttemptCount >= 0),
    ExternalEffectCount INTEGER NOT NULL CHECK (ExternalEffectCount >= 0),
    LastAllocatedOrdinal INTEGER NOT NULL CHECK (LastAllocatedOrdinal >= 0),
    LastFoldedOrdinal INTEGER NOT NULL CHECK (LastFoldedOrdinal >= 0),
    -- Order-sensitive: it commits to the exact sequence of folded and appended receipts, so a
    -- removed or reordered receipt cannot be hidden by rewriting a count.
    DisclosureChainDigest BLOB NOT NULL CHECK (length(DisclosureChainDigest) = 32),
    PRIMARY KEY (OriginInstallationId, SubjectKind, SubjectId),
    -- A live subject has no close time and a terminal one always does. Without this a reader cannot
    -- tell an abandoned subject from an open one that happened to record a timestamp.
    CHECK (
        (LifecycleCode IN (1, 2) AND ClosedAtUtc IS NULL)
        OR (LifecycleCode IN (3, 4) AND ClosedAtUtc IS NOT NULL)
    ),
    -- Folding can never run ahead of allocation. A folded watermark past the allocated one would
    -- silently skip receipts that have not been written yet.
    CHECK (LastFoldedOrdinal <= LastAllocatedOrdinal)
);

-- Startup sweeps prior-boot subjects, and the heartbeat monitor scans live ones.
CREATE INDEX IF NOT EXISTS idx_disclosure_subject_state_lifecycle_heartbeat
    ON disclosure_subject_state(LifecycleCode, LastHeartbeatAtUtc);

-- Compaction picks terminal subjects whose tail still has unfolded receipts.
CREATE INDEX IF NOT EXISTS idx_disclosure_subject_state_lifecycle_folded
    ON disclosure_subject_state(LifecycleCode, LastFoldedOrdinal);
