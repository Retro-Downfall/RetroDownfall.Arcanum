CREATE TRIGGER IF NOT EXISTS tapestry_nodes_legacy_update
AFTER UPDATE OF ParentNodeId ON tapestry_nodes
WHEN NEW.ParentNodeId IS NOT OLD.ParentNodeId
BEGIN
    DELETE FROM tapestry_edges WHERE GenerationId = OLD.GenerationId AND ParentNodeId = OLD.ParentNodeId AND ChildNodeId = OLD.NodeId AND MembershipReason = 'Legacy';
    INSERT INTO tapestry_edges (GenerationId, ParentNodeId, ChildNodeId, MembershipReason)
    SELECT NEW.GenerationId, NEW.ParentNodeId, NEW.NodeId, 'Legacy' WHERE NEW.ParentNodeId IS NOT NULL
    ON CONFLICT (GenerationId, ParentNodeId, ChildNodeId) DO NOTHING;
END;
