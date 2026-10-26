CREATE TRIGGER IF NOT EXISTS tapestry_node_embeddings_guard_update
BEFORE UPDATE ON tapestry_node_embeddings
WHEN NOT EXISTS (SELECT 1 FROM tapestry_nodes node JOIN tapestry_generations generation ON generation.GenerationId = node.GenerationId WHERE node.NodeId = NEW.NodeId AND generation.Status = 'Building') OR NEW.NodeId IS NOT OLD.NodeId
BEGIN
    SELECT RAISE(ABORT, 'Tapestry graph mutation violates its generation contract.');
END;
