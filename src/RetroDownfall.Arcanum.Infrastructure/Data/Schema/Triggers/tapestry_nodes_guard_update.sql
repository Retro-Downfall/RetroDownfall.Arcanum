CREATE TRIGGER IF NOT EXISTS tapestry_nodes_guard_update
BEFORE UPDATE ON tapestry_nodes
WHEN NOT EXISTS (SELECT 1 FROM tapestry_generations WHERE GenerationId = NEW.GenerationId AND ScopeKind = NEW.ScopeKind AND ScopeId = NEW.ScopeId AND Status = 'Building') OR NEW.NodeId IS NOT OLD.NodeId OR NEW.GenerationId IS NOT OLD.GenerationId OR NEW.ScopeKind IS NOT OLD.ScopeKind OR NEW.ScopeId IS NOT OLD.ScopeId OR NEW.Layer < 0 OR NEW.NodeKind NOT IN ('Leaf', 'Summary') OR (NEW.ParentNodeId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM tapestry_nodes parent WHERE parent.NodeId = NEW.ParentNodeId AND parent.GenerationId = NEW.GenerationId AND parent.ScopeKind = NEW.ScopeKind AND parent.ScopeId = NEW.ScopeId AND parent.NodeKind = 'Summary' AND parent.Layer > NEW.Layer))
    OR EXISTS (SELECT 1 FROM tapestry_edges edge JOIN tapestry_nodes child ON child.NodeId = edge.ChildNodeId WHERE edge.ParentNodeId = OLD.NodeId AND (NEW.NodeKind <> 'Summary' OR NEW.Layer <= child.Layer))
    OR EXISTS (SELECT 1 FROM tapestry_edges edge JOIN tapestry_nodes parent ON parent.NodeId = edge.ParentNodeId WHERE edge.ChildNodeId = OLD.NodeId AND NEW.Layer >= parent.Layer)
BEGIN
    SELECT RAISE(ABORT, 'Tapestry graph mutation violates its generation contract.');
END;
