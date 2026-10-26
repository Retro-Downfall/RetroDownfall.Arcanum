INSERT INTO tapestry_edges (GenerationId, ParentNodeId, ChildNodeId, MembershipReason)
SELECT GenerationId, ParentNodeId, NodeId, 'Legacy' FROM tapestry_nodes WHERE ParentNodeId IS NOT NULL;
