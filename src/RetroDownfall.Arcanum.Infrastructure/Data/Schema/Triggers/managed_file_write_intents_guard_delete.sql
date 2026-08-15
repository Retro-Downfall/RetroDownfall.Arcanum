-- Two disjoint branches, written as one rule so a caller cannot hold both scopes and satisfy the
-- weaker one. Ordinary retention removes only a row whose file question is settled: Cleaned,
-- ManualNonrevocable, or Erased. An AdoptedAndLabeled row still owns a real file and a real label, so
-- retaining it away would leave a labelled sensitive file with nothing in the database admitting to
-- owning it.
--
-- The restore-staging branch removes a row at any phase, because a restored row describes a file on a
-- machine this installation is not. It requires the exact immutable tombstone first, no local erasure
-- row still pointing at this producer, and a label disposition consistent with the phase: an adopted
-- source must have had its exact label removed in the same transaction, and every other phase must
-- have had no live label at all.
CREATE TRIGGER IF NOT EXISTS managed_file_write_intents_guard_delete
BEFORE DELETE ON managed_file_write_intents
BEGIN
    SELECT RAISE(ABORT, 'A managed write intent delete requires either terminal retention authorization or its exact restore-staging tombstone.')
    WHERE NOT (
        (
            arcanum_restore_staging_managed_authority_sanitization_authorized() = 0
            AND OLD.PhaseCode IN (8, 9, 10)
            AND (
                arcanum_sensitivity_purge_authorized() = 1
                OR arcanum_covenant_family_maintenance_authorized() = 1
                OR arcanum_owner_cleanup_authorized() = 1
            )
        )
        OR (
            arcanum_restore_staging_managed_authority_sanitization_authorized() = 1
            AND arcanum_sensitivity_purge_authorized() = 0
            AND arcanum_covenant_family_maintenance_authorized() = 0
            AND arcanum_owner_cleanup_authorized() = 0
            AND arcanum_managed_file_intent_mutation_authorized() = 0
            AND NOT EXISTS (
                SELECT 1
                FROM local_erasure_work_items
                WHERE SourceWriteOperationId = OLD.WriteOperationId
            )
            AND NOT EXISTS (
                SELECT 1
                FROM artifact_sensitivity
                WHERE LabelId = OLD.SensitivityLabelId
            )
            AND EXISTS (
                SELECT 1
                FROM restored_managed_file_authority_tombstones
                WHERE SourceKind = 1
                    AND SourceRowId = OLD.WriteOperationId
                    AND SourceWriteOperationId = OLD.WriteOperationId
                    AND ArtifactId = OLD.ArtifactId
                    AND SensitivityLabelId = OLD.SensitivityLabelId
                    AND OriginalStateCode = OLD.PhaseCode
                    AND (
                        (LabelDispositionCode = 2 AND OLD.PhaseCode = 7)
                        OR (LabelDispositionCode = 1 AND OLD.PhaseCode <> 7)
                    )
            )
        )
    );
END;
