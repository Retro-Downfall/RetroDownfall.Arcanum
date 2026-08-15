-- The crash-recovery inventory for deleting one Arcanum-owned file that lives outside SQLite. A
-- deletion crosses a boundary the database cannot roll back, so the authority to delete is copied
-- from the durable producer row before the first syscall and is never taken from the caller: a
-- caller-supplied root, revision, segment, parent identity, leaf, or ownership value would let a
-- confused or hostile request point the deleter at a file Arcanum never created.
CREATE TABLE IF NOT EXISTS local_erasure_work_items (
    WorkItemId TEXT NOT NULL PRIMARY KEY CHECK (length(WorkItemId) > 0),
    ErasureOperationId TEXT NOT NULL CHECK (length(ErasureOperationId) > 0),
    -- The managed_file_write_intents row this work item erases, plus the revision it was read at, so
    -- a producer that moved on cannot be terminalized by stale work.
    SourceWriteOperationId TEXT NOT NULL CHECK (length(SourceWriteOperationId) > 0),
    ExpectedSourceRevision INTEGER NOT NULL CHECK (ExpectedSourceRevision >= 0),
    ArtifactId TEXT NOT NULL CHECK (length(ArtifactId) > 0),
    SourceSensitivityLabelId TEXT NOT NULL CHECK (length(SourceSensitivityLabelId) > 0),
    -- Encrypted ManagedFileDurableLocationEvidence: canonical Campaign root identity digest,
    -- positive path revision, bounded normalized relative parent segments, the parent physical
    -- identity captured from the same retained no-follow handle, and the bounded target leaf. Size
    -- capped so a malformed row cannot become an unbounded blob in the core tier.
    DurableLocationEvidence BLOB NOT NULL CHECK (length(DurableLocationEvidence) BETWEEN 1 AND 8192),
    -- The ManagedFileOwnershipEvidence copied from the producer: reopened physical identity, full
    -- content hash, and checked length.
    ExpectedOwnershipEvidence BLOB NOT NULL CHECK (length(ExpectedOwnershipEvidence) BETWEEN 1 AND 4096),
    -- LocalErasureWorkItemState: Prepared = 1, DeletionVerified = 2, Completed = 3,
    -- ManualBlocker = 4.
    StateCode INTEGER NOT NULL CHECK (StateCode IN (1, 2, 3, 4)),
    -- LocalErasureDeletionEvidenceCode: AlreadyAbsent = 1,
    -- SameHandleDeletedAndParentFsynced = 2.
    DeletionEvidenceCode INTEGER NULL CHECK (DeletionEvidenceCode IS NULL OR DeletionEvidenceCode IN (1, 2)),
    CheckpointRevision INTEGER NOT NULL CHECK (CheckpointRevision >= 0),
    RetryCount INTEGER NOT NULL CHECK (RetryCount >= 0),
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    -- Evidence exists exactly where deletion was proven. Prepared has not looked yet and
    -- ManualBlocker deliberately touched nothing, so either carrying evidence would be a claim the
    -- work item never earned.
    CHECK (
        (StateCode IN (1, 4) AND DeletionEvidenceCode IS NULL)
        OR (StateCode IN (2, 3) AND DeletionEvidenceCode IS NOT NULL)
    )
);

-- One unfinished work item per producer. Two live items for the same source would both try to
-- terminalize it, and the second would find the label already gone and could not tell that from a
-- mismatch.
CREATE UNIQUE INDEX IF NOT EXISTS ux_local_erasure_work_items_active_source
    ON local_erasure_work_items(SourceWriteOperationId)
    WHERE StateCode IN (1, 2);

-- Restore staging and the managed-write delete guard both need every row linked to a producer,
-- including terminal ones.
CREATE INDEX IF NOT EXISTS idx_local_erasure_work_items_source
    ON local_erasure_work_items(SourceWriteOperationId);

CREATE INDEX IF NOT EXISTS idx_local_erasure_work_items_artifact
    ON local_erasure_work_items(ArtifactId);

-- Pre-readiness recovery enumerates nonterminal rows before any writer runs.
CREATE INDEX IF NOT EXISTS idx_local_erasure_work_items_state
    ON local_erasure_work_items(StateCode);
