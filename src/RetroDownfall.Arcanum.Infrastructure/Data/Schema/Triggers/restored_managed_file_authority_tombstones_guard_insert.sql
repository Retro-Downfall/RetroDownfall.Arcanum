-- A tombstone is the receipt that authority was stripped, and it is also the thing the two source
-- delete guards accept as proof. That makes writing one an authority operation in its own right, so
-- it requires the dedicated restore-staging predicate and refuses every ordinary scope: a nested
-- ordinary authorization would mean the caller is not the sealed staged connection this predicate is
-- supposed to identify.
--
-- Note that the sealed capability that can turn this predicate true is not built yet, so in
-- production today the predicate only ever returns zero and no tombstone can be written. The guard is
-- installed now so the delete branches it backs are never reachable through some other path.
--
-- The ordering rule is the second half. A local-erasure tombstone must copy its restore identity,
-- staged generation, artifact, label, owner scope, and label disposition from the already-inserted
-- source tombstone, which forces the sanitizer to inventory managed sources first and prevents a
-- local row from inventing a disposition its producer never recorded.
CREATE TRIGGER IF NOT EXISTS restored_managed_file_authority_tombstones_guard_insert
BEFORE INSERT ON restored_managed_file_authority_tombstones
BEGIN
    SELECT RAISE(ABORT, 'A restore-staging authority tombstone requires the sealed staging sanitization authorization alone.')
    WHERE arcanum_restore_staging_managed_authority_sanitization_authorized() = 0
        OR arcanum_managed_file_intent_mutation_authorized() = 1
        OR arcanum_sensitivity_purge_authorized() = 1
        OR arcanum_covenant_family_maintenance_authorized() = 1
        OR arcanum_owner_cleanup_authorized() = 1;

    SELECT RAISE(ABORT, 'A managed-write source tombstone must be keyed by its own write operation.')
    WHERE NEW.SourceKind = 1
        AND NEW.SourceRowId <> NEW.SourceWriteOperationId;

    SELECT RAISE(ABORT, 'A local erasure tombstone requires its exact already-inserted managed-write source tombstone.')
    WHERE NEW.SourceKind = 2
        AND NOT EXISTS (
            SELECT 1
            FROM restored_managed_file_authority_tombstones
            WHERE RestoreOperationId = NEW.RestoreOperationId
                AND SourceKind = 1
                AND SourceRowId = NEW.SourceWriteOperationId
                AND RestoreEffectDigest = NEW.RestoreEffectDigest
                AND StagedDatasetGeneration = NEW.StagedDatasetGeneration
                AND SourceWriteOperationId = NEW.SourceWriteOperationId
                AND ArtifactId = NEW.ArtifactId
                AND SensitivityLabelId = NEW.SensitivityLabelId
                AND OwnerScopeCode = NEW.OwnerScopeCode
                AND OwnerCampaignId IS NEW.OwnerCampaignId
                AND LabelDispositionCode = NEW.LabelDispositionCode
        );
END;
