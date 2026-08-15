-- A checkpoint is the reason recovery does not rerun a maintenance step that already committed its
-- artifact and advanced its watermark. Deleting one would make that step look unstarted while its
-- output and target revision remain, so the step would run again against a target it already moved.
-- Checkpoints leave only with their Session, through the guarded retention or owner cleanup
-- transaction that removes their claim.
CREATE TRIGGER IF NOT EXISTS session_turn_maintenance_steps_guard_delete
BEFORE DELETE ON session_turn_maintenance_steps
WHEN arcanum_session_retention_authorized() = 0
    AND arcanum_owner_cleanup_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'session_turn_maintenance_steps delete requires an authorized retention or owner cleanup scope.');
END;
