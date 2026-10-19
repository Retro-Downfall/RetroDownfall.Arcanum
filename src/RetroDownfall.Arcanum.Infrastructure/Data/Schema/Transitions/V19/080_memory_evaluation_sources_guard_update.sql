CREATE TRIGGER IF NOT EXISTS memory_evaluation_sources_guard_update
BEFORE UPDATE ON memory_evaluation_sources
BEGIN
    SELECT RAISE(ABORT, 'Evaluation evidence is immutable.');
END;
