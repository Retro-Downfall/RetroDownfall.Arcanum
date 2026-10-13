CREATE TRIGGER IF NOT EXISTS long_rest_node_delete
BEFORE DELETE ON long_rest_discovery_nodes
WHEN EXISTS (SELECT 1 FROM long_rest_discovery_generations WHERE GenerationId = OLD.GenerationId AND PhaseCode <> 7)
BEGIN
    UPDATE long_rest_discovery_generations SET PhaseCode = 7, Revision = Revision + 1, Published = 0
    WHERE GenerationId = OLD.GenerationId AND PhaseCode <> 7;
END;
