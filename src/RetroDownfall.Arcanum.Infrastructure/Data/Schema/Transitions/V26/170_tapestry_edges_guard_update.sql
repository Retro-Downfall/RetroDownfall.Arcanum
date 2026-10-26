CREATE TRIGGER IF NOT EXISTS tapestry_edges_guard_update
BEFORE UPDATE ON tapestry_edges
WHEN NOT EXISTS (SELECT 1 FROM tapestry_nodes parent JOIN tapestry_nodes child ON child.GenerationId = parent.GenerationId JOIN tapestry_generations generation ON generation.GenerationId = parent.GenerationId WHERE parent.NodeId = NEW.ParentNodeId AND child.NodeId = NEW.ChildNodeId AND parent.GenerationId = NEW.GenerationId AND parent.ScopeKind = child.ScopeKind AND parent.ScopeId = child.ScopeId AND parent.ScopeKind = generation.ScopeKind AND parent.ScopeId = generation.ScopeId AND parent.NodeKind = 'Summary' AND parent.Layer > child.Layer AND generation.Status = 'Building') OR NEW.GenerationId IS NOT OLD.GenerationId OR EXISTS (SELECT 1 FROM tapestry_generations WHERE GenerationId = OLD.GenerationId AND Status <> 'Building')
BEGIN
    SELECT RAISE(ABORT, 'Tapestry graph mutation violates its generation contract.');
END;
