CREATE TRIGGER IF NOT EXISTS tapestry_node_embeddings_guard_delete
BEFORE DELETE ON tapestry_node_embeddings
WHEN EXISTS (SELECT 1 FROM tapestry_nodes node JOIN tapestry_generations generation ON generation.GenerationId = node.GenerationId WHERE node.NodeId = OLD.NodeId AND generation.Status <> 'Building')
BEGIN
    SELECT RAISE(ABORT, 'Tapestry graph mutation violates its generation contract.');
END;
