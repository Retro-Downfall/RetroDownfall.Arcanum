CREATE TRIGGER IF NOT EXISTS tapestry_nodes_guard_delete
BEFORE DELETE ON tapestry_nodes
WHEN EXISTS (SELECT 1 FROM tapestry_generations WHERE GenerationId = OLD.GenerationId AND Status <> 'Building')
BEGIN
    SELECT RAISE(ABORT, 'Tapestry graph mutation violates its generation contract.');
END;
