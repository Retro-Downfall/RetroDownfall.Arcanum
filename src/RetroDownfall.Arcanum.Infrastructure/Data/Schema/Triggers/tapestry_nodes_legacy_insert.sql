CREATE TRIGGER IF NOT EXISTS tapestry_nodes_legacy_insert
AFTER INSERT ON tapestry_nodes
WHEN NEW.ParentNodeId IS NOT NULL
BEGIN
    INSERT INTO tapestry_edges (GenerationId, ParentNodeId, ChildNodeId, MembershipReason)
    SELECT NEW.GenerationId, NEW.ParentNodeId, NEW.NodeId, 'Legacy' WHERE NEW.ParentNodeId IS NOT NULL
    ON CONFLICT (GenerationId, ParentNodeId, ChildNodeId) DO NOTHING;
END;
