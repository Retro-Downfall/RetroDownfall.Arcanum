CREATE TRIGGER IF NOT EXISTS memory_evaluation_reports_validate_insert
BEFORE INSERT ON memory_evaluation_reports
BEGIN
    SELECT CASE WHEN EXISTS(SELECT 1 FROM memory_evaluation_reports WHERE EvaluationId = NEW.EvaluationId) THEN RAISE(ABORT, 'Evaluation evidence is immutable.') END;
    SELECT CASE WHEN NOT EXISTS(SELECT 1 FROM memory_evaluations WHERE EvaluationId = NEW.EvaluationId AND CreatedAtUtc <= NEW.CompletedAtUtc) THEN RAISE(ABORT, 'Terminal evaluation requires its retained registration.') END;
END;
