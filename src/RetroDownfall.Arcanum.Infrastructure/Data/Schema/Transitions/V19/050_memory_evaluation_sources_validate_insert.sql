CREATE TRIGGER IF NOT EXISTS memory_evaluation_sources_validate_insert
BEFORE INSERT ON memory_evaluation_sources
WHEN EXISTS(SELECT 1 FROM memory_evaluations WHERE EvaluationId = NEW.EvaluationId)
BEGIN
    SELECT RAISE(ABORT, 'Evaluation manifest is sealed.');
END;
