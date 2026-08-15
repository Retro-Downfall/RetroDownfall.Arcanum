-- Creating a work item is what grants the authority to delete a file. The authority is not taken
-- from the caller: this guard requires that an AdoptedAndLabeled producer row still exists at the
-- exact revision, artifact, and label the caller claims to have read, so the location and ownership
-- being copied come from a row Arcanum wrote when it created the file. A caller that supplies its own
-- root, leaf, or ownership values cannot get past this check.
--
-- Managed-write authorization is explicitly refused. That scope belongs to the writer, and letting it
-- also open erasure work would make one borrowed scope able to both create and destroy.
CREATE TRIGGER IF NOT EXISTS local_erasure_work_items_guard_insert
BEFORE INSERT ON local_erasure_work_items
BEGIN
    SELECT RAISE(ABORT, 'A local erasure work item insert requires retention-purge or family-maintenance authorization.')
    WHERE arcanum_sensitivity_purge_authorized() = 0
        AND arcanum_covenant_family_maintenance_authorized() = 0;

    SELECT RAISE(ABORT, 'A local erasure work item never accepts managed-write or restore-staging authorization.')
    WHERE arcanum_managed_file_intent_mutation_authorized() = 1
        OR arcanum_restore_staging_managed_authority_sanitization_authorized() = 1;

    SELECT RAISE(ABORT, 'A local erasure work item is created Prepared, at revision zero, with no deletion evidence.')
    WHERE NEW.StateCode <> 1
        OR NEW.CheckpointRevision <> 0
        OR NEW.DeletionEvidenceCode IS NOT NULL;

    SELECT RAISE(ABORT, 'A local erasure work item requires its exact AdoptedAndLabeled producer row at the expected revision.')
    WHERE NOT EXISTS (
        SELECT 1
        FROM managed_file_write_intents
        WHERE WriteOperationId = NEW.SourceWriteOperationId
            AND PhaseCode = 7
            AND Revision = NEW.ExpectedSourceRevision
            AND ArtifactId = NEW.ArtifactId
            AND SensitivityLabelId = NEW.SourceSensitivityLabelId
    );

    SELECT RAISE(ABORT, 'A local erasure work item requires the exact live sensitivity label of its producer.')
    WHERE NOT EXISTS (
        SELECT 1
        FROM artifact_sensitivity
        WHERE LabelId = NEW.SourceSensitivityLabelId
            AND ArtifactId = NEW.ArtifactId
    );
END;
