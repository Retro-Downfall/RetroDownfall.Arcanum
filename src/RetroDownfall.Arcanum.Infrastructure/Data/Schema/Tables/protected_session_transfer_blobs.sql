-- One row per attachment blob a protected transfer must stage, written in full before the first
-- filesystem byte. That ordering is the point: after a crash, recovery can enumerate, verify, and
-- compare-delete every file this operation could possibly have created, without holding the source
-- lease that produced them. It stores no attachment bytes, no source capability, and no live handle,
-- because none of those survive a process exit and persisting them would only invite a stale
-- capability to be replayed.
CREATE TABLE IF NOT EXISTS protected_session_transfer_blobs (
    OperationId TEXT NOT NULL CHECK (length(OperationId) > 0),
    BlobOrdinal INTEGER NOT NULL CHECK (BlobOrdinal >= 0),
    -- Encrypted durable parent identity, revalidated from the retained no-follow handle before
    -- either leaf is opened.
    DurableParentIdentityEvidence BLOB NOT NULL CHECK (
        length(DurableParentIdentityEvidence) BETWEEN 1 AND 8192
    ),
    TemporaryLeaf BLOB NOT NULL CHECK (length(TemporaryLeaf) BETWEEN 1 AND 1024),
    FinalLeaf BLOB NOT NULL CHECK (length(FinalLeaf) BETWEEN 1 AND 1024),
    ExpectedContentHash BLOB NOT NULL CHECK (length(ExpectedContentHash) = 32),
    ExpectedContentLength INTEGER NOT NULL CHECK (ExpectedContentLength >= 0),
    -- Filled from the same reopened handle that verified the content, so a file replaced underneath
    -- with matching bytes still fails identity.
    ObservedPhysicalIdentity BLOB NULL CHECK (
        ObservedPhysicalIdentity IS NULL OR length(ObservedPhysicalIdentity) = 32
    ),
    -- Prepared = 1, TempCreated = 2, TempWritten = 3, TempFsynced = 4, RenamedNoReplace = 5,
    -- ParentFsynced = 6, ReopenedVerified = 7, Referenced = 8, Cleaned = 9.
    PhaseCode INTEGER NOT NULL CHECK (PhaseCode IN (1, 2, 3, 4, 5, 6, 7, 8, 9)),
    Revision INTEGER NOT NULL CHECK (Revision >= 0),
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    PRIMARY KEY (OperationId, BlobOrdinal),
    -- The two leaves are distinct names under the same parent. A rename onto itself would destroy
    -- the no-replace guarantee the staging sequence depends on.
    CHECK (TemporaryLeaf <> FinalLeaf),
    -- Identity is observed only after the file has been reopened. Before ParentFsynced there is
    -- nothing durable to observe; Cleaned accepts either, because a crash may precede file creation
    -- entirely and proven absence is a legitimate way to reach it.
    CHECK (
        (PhaseCode BETWEEN 1 AND 6 AND ObservedPhysicalIdentity IS NULL)
        OR (PhaseCode IN (7, 8) AND ObservedPhysicalIdentity IS NOT NULL)
        OR PhaseCode = 9
    ),
    -- The parent journal owns the children. Cascade is safe here and only here, because the parent
    -- delete guard already refuses to remove a transfer before its post-disposition terminal phase.
    FOREIGN KEY (OperationId)
        REFERENCES protected_session_transfer_intents(OperationId) ON DELETE CASCADE
);

-- Recovery walks one operation's children in ordinal order, and the database-commit step selects
-- every verified child at once.
CREATE INDEX IF NOT EXISTS idx_protected_session_transfer_blobs_operation_phase
    ON protected_session_transfer_blobs(OperationId, PhaseCode);
