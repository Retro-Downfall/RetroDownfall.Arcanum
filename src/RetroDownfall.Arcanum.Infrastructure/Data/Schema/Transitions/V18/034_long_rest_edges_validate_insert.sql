CREATE TRIGGER IF NOT EXISTS long_rest_edges_validate_insert
BEFORE INSERT ON long_rest_discovery_edges
WHEN NOT EXISTS (SELECT 1 FROM long_rest_discovery_nodes parent JOIN long_rest_discovery_nodes child
    ON child.GenerationId = parent.GenerationId AND child.Layer >= 0 AND child.Layer < parent.Layer
    WHERE parent.GenerationId = NEW.GenerationId AND parent.NodeId = NEW.ParentNodeId AND child.NodeId = NEW.ChildNodeId)
BEGIN
    SELECT RAISE(ABORT, 'Long Rest edges require strictly descending layers in the same generation.');
END;
