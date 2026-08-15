-- Two disjoint branches, written as one rule because they must never overlap. A caller holding both
-- an ordinary retention scope and the staging scope would satisfy the weaker of the two, and the
-- staging path exists precisely to bypass the terminal-state requirement, so overlap would let live
-- work be deleted under a restore pretext.
--
-- Ordinary retention removes only a terminal item, once the deletion it records is finished. Restore
-- staging removes any item regardless of state, because a restored row describes a file on another
-- machine, but only after the exact immutable tombstone for that row already exists.
CREATE TRIGGER IF NOT EXISTS local_erasure_work_items_guard_delete
BEFORE DELETE ON local_erasure_work_items
BEGIN
    SELECT RAISE(ABORT, 'A local erasure work item delete requires either terminal retention authorization or its exact restore-staging tombstone.')
    WHERE NOT (
        (
            arcanum_restore_staging_managed_authority_sanitization_authorized() = 0
            AND OLD.StateCode IN (3, 4)
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
            AND EXISTS (
                SELECT 1
                FROM restored_managed_file_authority_tombstones
                WHERE SourceKind = 2
                    AND SourceRowId = OLD.WorkItemId
                    AND SourceWriteOperationId = OLD.SourceWriteOperationId
                    AND ArtifactId = OLD.ArtifactId
                    AND SensitivityLabelId = OLD.SourceSensitivityLabelId
                    AND OriginalStateCode = OLD.StateCode
            )
        )
    );
END;
