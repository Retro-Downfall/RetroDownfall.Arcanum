-- The row must exist, committed, before the first filesystem byte, and it must start in the one shape
-- that says "nothing has happened yet". A row inserted at a later phase would claim evidence no step
-- produced: a created-child identity nothing observed, or ownership of a file nobody verified.
--
-- The erasure and staging scopes are refused outright. Only the writer creates these rows, and an
-- erasure scope that could also insert one could manufacture its own deletion target.
CREATE TRIGGER IF NOT EXISTS managed_file_write_intents_guard_insert
BEFORE INSERT ON managed_file_write_intents
BEGIN
    SELECT RAISE(ABORT, 'A managed write intent insert requires managed-file intent authorization.')
    WHERE arcanum_managed_file_intent_mutation_authorized() = 0;

    SELECT RAISE(ABORT, 'A managed write intent insert never accepts an erasure or restore-staging authorization.')
    WHERE arcanum_sensitivity_purge_authorized() = 1
        OR arcanum_covenant_family_maintenance_authorized() = 1
        OR arcanum_restore_staging_managed_authority_sanitization_authorized() = 1;

    SELECT RAISE(ABORT, 'A managed write intent is created Prepared, at revision zero, with its pending label and no physical evidence.')
    WHERE NEW.PhaseCode <> 1
        OR NEW.Revision <> 0
        OR NEW.PendingArtifactSensitivityLabel IS NULL
        OR NEW.CreatedChildPhysicalIdentityDigest IS NOT NULL
        OR NEW.FinalOwnershipEvidence IS NOT NULL;
END;
