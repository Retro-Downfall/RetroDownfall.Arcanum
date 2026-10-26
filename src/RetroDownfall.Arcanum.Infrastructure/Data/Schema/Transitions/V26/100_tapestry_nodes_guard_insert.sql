CREATE TRIGGER IF NOT EXISTS tapestry_nodes_guard_insert
BEFORE INSERT ON tapestry_nodes
WHEN NOT EXISTS (SELECT 1 FROM tapestry_generations WHERE GenerationId = NEW.GenerationId AND ScopeKind = NEW.ScopeKind AND ScopeId = NEW.ScopeId AND Status = 'Building') OR NEW.Layer < 0 OR NEW.NodeKind NOT IN ('Leaf', 'Summary') OR (NEW.ParentNodeId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM tapestry_nodes parent WHERE parent.NodeId = NEW.ParentNodeId AND parent.GenerationId = NEW.GenerationId AND parent.ScopeKind = NEW.ScopeKind AND parent.ScopeId = NEW.ScopeId AND parent.NodeKind = 'Summary' AND parent.Layer > NEW.Layer)) OR EXISTS (SELECT 1 FROM tapestry_nodes WHERE NodeId = NEW.NodeId)
BEGIN
    SELECT RAISE(ABORT, 'Tapestry graph mutation violates its generation contract.');
END;
