CREATE TRIGGER IF NOT EXISTS saga_committed_uses_guard_update
BEFORE UPDATE ON saga_committed_uses
BEGIN
    SELECT RAISE(ABORT, 'Committed Saga use evidence is immutable.');
END;
