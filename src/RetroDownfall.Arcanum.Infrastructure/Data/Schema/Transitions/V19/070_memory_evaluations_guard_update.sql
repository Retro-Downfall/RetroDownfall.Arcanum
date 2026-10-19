CREATE TRIGGER IF NOT EXISTS memory_evaluations_guard_update
BEFORE UPDATE ON memory_evaluations
BEGIN
    SELECT RAISE(ABORT, 'Evaluation evidence is immutable.');
END;
