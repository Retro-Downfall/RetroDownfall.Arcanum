CREATE TRIGGER IF NOT EXISTS tapestry_generations_guard_insert
BEFORE INSERT ON tapestry_generations
WHEN EXISTS (SELECT 1 FROM tapestry_generations WHERE GenerationId = NEW.GenerationId)
BEGIN
    SELECT RAISE(ABORT, 'Tapestry graph mutation violates its generation contract.');
END;
