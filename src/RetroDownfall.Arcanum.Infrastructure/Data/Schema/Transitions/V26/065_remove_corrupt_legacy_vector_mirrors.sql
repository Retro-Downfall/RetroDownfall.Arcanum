-- Run only for an existing plain mirror, classified inside the transition transaction.
-- Match the whole-generation rejection in the following owner-delete statement.
DELETE FROM tapestry_node_embeddings_vec
WHERE NodeId IN (
    SELECT NodeId FROM tapestry_nodes WHERE GenerationId IN (
        SELECT GenerationId FROM tapestry_generations WHERE EXISTS (

    SELECT 1 FROM tapestry_nodes child LEFT JOIN tapestry_nodes parent ON parent.NodeId = child.ParentNodeId
    WHERE child.GenerationId = tapestry_generations.GenerationId
        AND (child.ScopeKind IS NOT tapestry_generations.ScopeKind OR child.ScopeId IS NOT tapestry_generations.ScopeId
            OR child.Layer < 0 OR child.NodeKind NOT IN ('Leaf', 'Summary')
            OR (child.ParentNodeId IS NOT NULL AND (parent.NodeId IS NULL
                OR parent.GenerationId IS NOT child.GenerationId OR parent.ScopeKind IS NOT child.ScopeKind
                OR parent.ScopeId IS NOT child.ScopeId OR parent.NodeKind <> 'Summary' OR parent.Layer <= child.Layer))))));
