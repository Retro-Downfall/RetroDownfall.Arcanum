-- Every transition here is preceded or followed by an effect the database cannot roll back, so the
-- row must describe exactly one reality at a time. Location, copied ownership, producer identity,
-- artifact, and label are immutable because retargeting them mid-flight would point a proven deletion
-- at a different file. The compare-and-swap on CheckpointRevision keeps two recovery passes from both
-- advancing the same item, and the closed edge list keeps a Prepared item from claiming the deletion
-- evidence only a verification step can produce.
--
-- Completion is the ordering constraint that matters most: the work item may only become Completed
-- after its producer row is already Erased, so the source of ownership is terminalized before the
-- record of the deletion is.
CREATE TRIGGER IF NOT EXISTS local_erasure_work_items_guard_update
BEFORE UPDATE ON local_erasure_work_items
BEGIN
    SELECT RAISE(ABORT, 'A local erasure work item update requires retention-purge or family-maintenance authorization.')
    WHERE arcanum_sensitivity_purge_authorized() = 0
        AND arcanum_covenant_family_maintenance_authorized() = 0;

    SELECT RAISE(ABORT, 'A local erasure work item never accepts managed-write or restore-staging authorization.')
    WHERE arcanum_managed_file_intent_mutation_authorized() = 1
        OR arcanum_restore_staging_managed_authority_sanitization_authorized() = 1;

    SELECT RAISE(ABORT, 'A local erasure work item identity, producer binding, location, and copied ownership are immutable.')
    WHERE NEW.WorkItemId <> OLD.WorkItemId
        OR NEW.ErasureOperationId <> OLD.ErasureOperationId
        OR NEW.SourceWriteOperationId <> OLD.SourceWriteOperationId
        OR NEW.ExpectedSourceRevision <> OLD.ExpectedSourceRevision
        OR NEW.ArtifactId <> OLD.ArtifactId
        OR NEW.SourceSensitivityLabelId <> OLD.SourceSensitivityLabelId
        OR NEW.DurableLocationEvidence <> OLD.DurableLocationEvidence
        OR NEW.ExpectedOwnershipEvidence <> OLD.ExpectedOwnershipEvidence
        OR NEW.CreatedAtUtc <> OLD.CreatedAtUtc;

    SELECT RAISE(ABORT, 'A local erasure work item update requires the exact prior checkpoint revision.')
    WHERE NEW.CheckpointRevision <> OLD.CheckpointRevision + 1;

    SELECT RAISE(ABORT, 'A local erasure work item follows only Prepared to DeletionVerified, Prepared to ManualBlocker, or DeletionVerified to Completed.')
    WHERE NOT (
        (OLD.StateCode = 1 AND NEW.StateCode IN (2, 4))
        OR (OLD.StateCode = 2 AND NEW.StateCode = 3)
    );

    SELECT RAISE(ABORT, 'A local erasure work item may only complete after its producer row is already Erased.')
    WHERE NEW.StateCode = 3
        AND NOT EXISTS (
            SELECT 1
            FROM managed_file_write_intents
            WHERE WriteOperationId = OLD.SourceWriteOperationId
                AND PhaseCode = 10
        );
END;
