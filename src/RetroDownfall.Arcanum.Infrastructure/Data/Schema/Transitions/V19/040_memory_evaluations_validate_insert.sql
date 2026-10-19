CREATE TRIGGER IF NOT EXISTS memory_evaluations_validate_insert
BEFORE INSERT ON memory_evaluations
BEGIN
    SELECT CASE WHEN EXISTS(SELECT 1 FROM memory_evaluations WHERE EvaluationId = NEW.EvaluationId OR OperationId = NEW.OperationId) THEN RAISE(ABORT, 'Evaluation evidence is immutable.') END;
    SELECT CASE WHEN (SELECT count(*) FROM memory_evaluations) >= 1024 THEN RAISE(ABORT, 'Evaluation quota requires report-only cleanup.') END;
    SELECT CASE WHEN (SELECT count(*) FROM memory_evaluation_sources WHERE EvaluationId = NEW.EvaluationId) <> NEW.SourceCount THEN RAISE(ABORT, 'Evaluation manifest must be complete before sealing.') END;
END;
