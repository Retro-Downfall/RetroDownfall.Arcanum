-- Restore never inherits managed-file authority. A backup taken on one machine describes files that
-- do not exist on this one, so every restored managed-write and local-erasure row is stripped during
-- staging and replaced by one of these tombstones. The tombstone exists for audit correlation only:
-- it deliberately retains no root identity, path revision, parent segment, leaf, parent or child
-- physical identity, created-child identity, final ownership, expected hash or length, pending label
-- projection, deletion evidence, serialized location, or opener input, because any one of those
-- would let a later caller reconstruct a delete capability for a path this installation never owned.
CREATE TABLE IF NOT EXISTS restored_managed_file_authority_tombstones (
    -- The authenticated CovenantExclusiveOperation.BackupRestore operation that stripped the row.
    RestoreOperationId TEXT NOT NULL CHECK (length(RestoreOperationId) > 0),
    -- RestoredManagedFileAuthoritySourceKind: ManagedWriteIntent = 1, LocalErasureWorkItem = 2.
    SourceKind INTEGER NOT NULL CHECK (SourceKind IN (1, 2)),
    SourceRowId TEXT NOT NULL CHECK (length(SourceRowId) > 0),
    RestoreEffectDigest BLOB NOT NULL CHECK (length(RestoreEffectDigest) = 32),
    StagedDatasetGeneration BLOB NOT NULL CHECK (length(StagedDatasetGeneration) = 16),
    SourceWriteOperationId TEXT NOT NULL CHECK (length(SourceWriteOperationId) > 0),
    ArtifactId TEXT NOT NULL CHECK (length(ArtifactId) > 0),
    SensitivityLabelId TEXT NOT NULL CHECK (length(SensitivityLabelId) > 0),
    -- The phase or state the stripped row was in. One through ten for a managed write intent, one
    -- through four for a local erasure work item.
    OriginalStateCode INTEGER NOT NULL CHECK (OriginalStateCode > 0),
    -- CovenantScope: Global = 1, Campaign = 2.
    OwnerScopeCode INTEGER NOT NULL CHECK (OwnerScopeCode IN (1, 2)),
    -- Historical: the Campaign this authority belonged to on the source machine, with no foreign key
    -- because that Campaign may not exist here at all.
    OwnerCampaignId TEXT NULL,
    -- RestoredManagedFileLabelDisposition: NoLiveLabel = 1, ExactLabelRemoved = 2.
    LabelDispositionCode INTEGER NOT NULL CHECK (LabelDispositionCode IN (1, 2)),
    -- A domain-separated commitment to the complete removed authority projection. It proves what was
    -- stripped without being convertible back into any of it.
    StrippedAuthorityDigest BLOB NOT NULL CHECK (length(StrippedAuthorityDigest) = 32),
    RecordedAtUtc TEXT NOT NULL,
    PRIMARY KEY (RestoreOperationId, SourceKind, SourceRowId),
    -- Scope and Campaign agree or the owner is ambiguous.
    CHECK (
        (OwnerScopeCode = 1 AND OwnerCampaignId IS NULL)
        OR (OwnerScopeCode = 2 AND OwnerCampaignId IS NOT NULL)
    ),
    -- Each source kind has its own closed state range, so a work-item state cannot be recorded as a
    -- write phase and pass a later disposition check meant for the other table.
    CHECK (
        (SourceKind = 1 AND OriginalStateCode BETWEEN 1 AND 10)
        OR (SourceKind = 2 AND OriginalStateCode BETWEEN 1 AND 4)
    )
);

-- The sanitizer links a local-erasure tombstone to its already-inserted source tombstone, and both
-- delete guards look their tombstone up by the producing write operation.
CREATE INDEX IF NOT EXISTS idx_restored_managed_file_authority_tombstones_source_write
    ON restored_managed_file_authority_tombstones(SourceWriteOperationId);

CREATE INDEX IF NOT EXISTS idx_restored_managed_file_authority_tombstones_artifact
    ON restored_managed_file_authority_tombstones(ArtifactId);
