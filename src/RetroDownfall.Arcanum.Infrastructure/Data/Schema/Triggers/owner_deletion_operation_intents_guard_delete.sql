-- An unfinished intent is the only record that a Campaign was deleted by a specific operation whose
-- workspace-marker cleanup has not finished. Deleting it early would strand that cleanup with no
-- owner to resume it, so retention may only remove a row the composite finalizer already completed.
CREATE TRIGGER IF NOT EXISTS owner_deletion_operation_intents_guard_delete
BEFORE DELETE ON owner_deletion_operation_intents
BEGIN
    SELECT RAISE(ABORT, 'An owner-deletion intent delete requires owner-cleanup authorization.')
    WHERE arcanum_owner_cleanup_authorized() = 0;

    SELECT RAISE(ABORT, 'Only a Completed owner-deletion intent may be retained away.')
    WHERE OLD.PhaseCode <> 4;
END;
