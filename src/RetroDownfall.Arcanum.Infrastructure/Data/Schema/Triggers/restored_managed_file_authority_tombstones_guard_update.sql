-- The two source delete guards accept a tombstone as proof that authority was stripped. If a
-- tombstone could be edited, that proof could be retargeted at a row it was never written for, so
-- update is forbidden unconditionally rather than under any authorization. A correction is a new
-- tombstone under a new restore operation, never a rewrite.
CREATE TRIGGER IF NOT EXISTS restored_managed_file_authority_tombstones_guard_update
BEFORE UPDATE ON restored_managed_file_authority_tombstones
BEGIN
    SELECT RAISE(ABORT, 'A restore-staging authority tombstone is immutable and can never be updated.');
END;
