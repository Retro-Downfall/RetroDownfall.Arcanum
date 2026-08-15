-- A Committed checkpoint was written in the same transaction as the artifact and label it names, so
-- recovery reuses that artifact instead of calling the provider again. Editing a committed row would
-- point recovery at a different artifact than the one whose digests were verified, which is exactly
-- the substitution the manifest and sensitivity digests exist to prevent. A committed step is
-- therefore frozen outright.
--
-- The frozen input revisions are what prove the step ran against the history it claims, so they
-- cannot be refreshed to make a stale checkpoint look current. Every other transition is a
-- compare-and-swap: the checkpoint revision has to advance, which is what makes two concurrent
-- executors resolve to one winner rather than both believing they own the step.
CREATE TRIGGER IF NOT EXISTS session_turn_maintenance_steps_validate_update
BEFORE UPDATE ON session_turn_maintenance_steps
BEGIN
    SELECT RAISE(ABORT, 'A committed maintenance checkpoint is immutable.')
    WHERE OLD.CheckpointStateCode = 2;

    SELECT RAISE(ABORT, 'A maintenance checkpoint cannot change the claim or step it belongs to.')
    WHERE NEW.ClaimId <> OLD.ClaimId OR NEW.StepCode <> OLD.StepCode;

    SELECT RAISE(ABORT, 'A maintenance checkpoint cannot change the history it was prepared against.')
    WHERE NEW.InputHistoryRevision <> OLD.InputHistoryRevision
        OR NEW.InputSensitivityRevision <> OLD.InputSensitivityRevision;

    SELECT RAISE(ABORT, 'A maintenance checkpoint revision must advance on every transition.')
    WHERE NEW.CheckpointRevision <= OLD.CheckpointRevision;

    -- Prepared may commit, fail, or be re-prepared under a new physical attempt; Failed may only be
    -- re-prepared or stay failed. Nothing reaches a committed output except through Prepared.
    SELECT RAISE(ABORT, 'session_turn_maintenance_steps accepts only its closed checkpoint transitions.')
    WHERE NOT (
        (OLD.CheckpointStateCode = 1 AND NEW.CheckpointStateCode IN (1, 2, 3))
        OR (OLD.CheckpointStateCode = 3 AND NEW.CheckpointStateCode IN (1, 3))
    );
END;
