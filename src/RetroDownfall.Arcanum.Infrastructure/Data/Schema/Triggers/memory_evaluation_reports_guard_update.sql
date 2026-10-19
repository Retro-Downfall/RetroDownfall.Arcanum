CREATE TRIGGER IF NOT EXISTS memory_evaluation_reports_guard_update
BEFORE UPDATE ON memory_evaluation_reports
BEGIN
    SELECT RAISE(ABORT, 'Evaluation evidence is immutable.');
END;
