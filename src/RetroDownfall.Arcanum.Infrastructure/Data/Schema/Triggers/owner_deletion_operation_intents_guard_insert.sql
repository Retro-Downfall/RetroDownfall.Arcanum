-- Preparing an owner-deletion intent is what makes a later Campaign deletion attributable, so the
-- ability to write one is authority, not data entry. The authorization begins false on every
-- connection, which means direct SQL and any caller that did not deliberately open the scope reaches
-- this guard and aborts. An operation ID and effect digest by themselves grant nothing.
CREATE TRIGGER IF NOT EXISTS owner_deletion_operation_intents_guard_insert
BEFORE INSERT ON owner_deletion_operation_intents
BEGIN
    SELECT RAISE(ABORT, 'An owner-deletion intent insert requires owner-cleanup authorization.')
    WHERE arcanum_owner_cleanup_authorized() = 0;

    SELECT RAISE(ABORT, 'An owner-deletion intent is created in its Prepared phase at revision zero.')
    WHERE NEW.PhaseCode <> 1
        OR NEW.Revision <> 0;
END;
