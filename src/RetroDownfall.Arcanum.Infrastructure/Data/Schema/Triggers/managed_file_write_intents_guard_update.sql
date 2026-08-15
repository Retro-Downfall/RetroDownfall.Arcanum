-- This trigger selects its authorization by the exact phase edge, which is unusual and deliberate.
-- Every writer and write-recovery edge belongs to the managed-file scope, and both erasure scopes
-- must be closed while it runs. The single AdoptedAndLabeled to Erased edge is the opposite: it is
-- the moment ownership ends rather than begins, so it refuses the managed-file scope and requires
-- exactly one erasure scope. Requiring exactly one, rather than at least one, means a caller cannot
-- hold both and let the trigger pick whichever explanation fits.
--
-- The evidence rules are one-time fills. CreatedChildPhysicalIdentityDigest is the only proof that a
-- child on disk was created by this operation, so it is written once on Prepared to TempCreated and
-- is otherwise byte-identical across every edge, including the terminal ones. FinalOwnershipEvidence
-- is written once on ParentFsynced to AdoptedAndLabeled and preserved into Erased. Letting either be
-- refilled would let a later step substitute a different file for the one this operation made.
CREATE TRIGGER IF NOT EXISTS managed_file_write_intents_guard_update
BEFORE UPDATE ON managed_file_write_intents
BEGIN
    SELECT RAISE(ABORT, 'A managed write intent update never accepts restore-staging authorization.')
    WHERE arcanum_restore_staging_managed_authority_sanitization_authorized() = 1;

    SELECT RAISE(ABORT, 'A managed write intent identity, artifact, label, location, and expected content are immutable.')
    WHERE NEW.WriteOperationId <> OLD.WriteOperationId
        OR NEW.StableEffectIdentityDigest <> OLD.StableEffectIdentityDigest
        OR NEW.ArtifactId <> OLD.ArtifactId
        OR NEW.SensitivityLabelId <> OLD.SensitivityLabelId
        OR NEW.SensitivityLabelDigest <> OLD.SensitivityLabelDigest
        OR NEW.DurableLocationEvidence <> OLD.DurableLocationEvidence
        OR NEW.ExpectedContentHash <> OLD.ExpectedContentHash
        OR NEW.ExpectedContentLength <> OLD.ExpectedContentLength
        OR NEW.CreatedAtUtc <> OLD.CreatedAtUtc;

    SELECT RAISE(ABORT, 'A managed write intent update requires the exact prior revision.')
    WHERE NEW.Revision <> OLD.Revision + 1;

    SELECT RAISE(ABORT, 'A managed write intent follows only one step forward through ParentFsynced, a terminal clean or manual outcome, or adoption to erasure.')
    WHERE NOT (
        (OLD.PhaseCode BETWEEN 1 AND 6 AND NEW.PhaseCode = OLD.PhaseCode + 1)
        OR (OLD.PhaseCode BETWEEN 1 AND 6 AND NEW.PhaseCode IN (8, 9))
        OR (OLD.PhaseCode = 7 AND NEW.PhaseCode = 10)
    );

    SELECT RAISE(ABORT, 'A pending sensitivity label projection is byte-for-byte immutable through ParentFsynced.')
    WHERE NEW.PhaseCode BETWEEN 1 AND 6
        AND NEW.PendingArtifactSensitivityLabel IS NOT OLD.PendingArtifactSensitivityLabel;

    SELECT RAISE(ABORT, 'A created-child physical identity is filled exactly once on Prepared to TempCreated and never changes afterward.')
    WHERE NOT (
        (
            OLD.PhaseCode = 1
            AND NEW.PhaseCode = 2
            AND OLD.CreatedChildPhysicalIdentityDigest IS NULL
            AND NEW.CreatedChildPhysicalIdentityDigest IS NOT NULL
        )
        OR (
            NOT (OLD.PhaseCode = 1 AND NEW.PhaseCode = 2)
            AND NEW.CreatedChildPhysicalIdentityDigest IS OLD.CreatedChildPhysicalIdentityDigest
        )
    );

    SELECT RAISE(ABORT, 'Final ownership evidence is filled exactly once on ParentFsynced to AdoptedAndLabeled and never changes afterward.')
    WHERE NOT (
        (
            OLD.PhaseCode = 6
            AND NEW.PhaseCode = 7
            AND OLD.FinalOwnershipEvidence IS NULL
            AND NEW.FinalOwnershipEvidence IS NOT NULL
        )
        OR (
            NOT (OLD.PhaseCode = 6 AND NEW.PhaseCode = 7)
            AND NEW.FinalOwnershipEvidence IS OLD.FinalOwnershipEvidence
        )
    );

    SELECT RAISE(ABORT, 'Adoption requires the persisted created-child physical identity.')
    WHERE NEW.PhaseCode = 7
        AND NEW.CreatedChildPhysicalIdentityDigest IS NULL;

    SELECT RAISE(ABORT, 'Every managed write phase edge except adoption to erasure requires managed-file intent authorization with both erasure scopes closed.')
    WHERE NEW.PhaseCode <> 10
        AND (
            arcanum_managed_file_intent_mutation_authorized() = 0
            OR arcanum_sensitivity_purge_authorized() = 1
            OR arcanum_covenant_family_maintenance_authorized() = 1
        );

    SELECT RAISE(ABORT, 'The adoption to erasure edge refuses managed-file intent authorization and requires exactly one erasure scope.')
    WHERE NEW.PhaseCode = 10
        AND (
            arcanum_managed_file_intent_mutation_authorized() = 1
            OR (arcanum_sensitivity_purge_authorized() + arcanum_covenant_family_maintenance_authorized()) <> 1
        );

    SELECT RAISE(ABORT, 'The adoption to erasure edge requires its exact DeletionVerified local erasure work item.')
    WHERE NEW.PhaseCode = 10
        AND NOT EXISTS (
            SELECT 1
            FROM local_erasure_work_items
            WHERE SourceWriteOperationId = OLD.WriteOperationId
                AND StateCode = 2
                AND ArtifactId = OLD.ArtifactId
                AND SourceSensitivityLabelId = OLD.SensitivityLabelId
        );

    SELECT RAISE(ABORT, 'The adoption to erasure edge requires the exact sensitivity label to be removed first in the same transaction.')
    WHERE NEW.PhaseCode = 10
        AND EXISTS (
            SELECT 1
            FROM artifact_sensitivity
            WHERE LabelId = OLD.SensitivityLabelId
        );
END;
