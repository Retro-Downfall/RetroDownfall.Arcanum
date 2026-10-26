CREATE TRIGGER IF NOT EXISTS tapestry_edges_guard_delete
BEFORE DELETE ON tapestry_edges
WHEN EXISTS (SELECT 1 FROM tapestry_generations WHERE GenerationId = OLD.GenerationId AND Status <> 'Building')
BEGIN
    SELECT RAISE(ABORT, 'Tapestry graph mutation violates its generation contract.');
END;
