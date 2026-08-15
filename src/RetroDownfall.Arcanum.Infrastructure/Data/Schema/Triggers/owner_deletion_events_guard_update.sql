-- The journal is a sequence of facts about deletions that already happened. Capabilities track their
-- own applied sequence against it, so rewriting a row would silently change the meaning of every
-- cursor that already passed it. Corrections arrive as new events, never as edits.
CREATE TRIGGER IF NOT EXISTS owner_deletion_events_guard_update
BEFORE UPDATE ON owner_deletion_events
BEGIN
    SELECT RAISE(ABORT, 'owner_deletion_events is append-only; an existing event cannot be updated.');
END;
