-- Strictly descending layers prove acyclicity without an unbounded recursive walk.
-- Discard the whole derived generation, never rewrite a source or invent a parent.
DELETE FROM tapestry_generations
WHERE EXISTS (
    SELECT 1 FROM tapestry_nodes child LEFT JOIN tapestry_nodes parent ON parent.NodeId = child.ParentNodeId
    WHERE child.GenerationId = tapestry_generations.GenerationId
        AND (child.ScopeKind IS NOT tapestry_generations.ScopeKind OR child.ScopeId IS NOT tapestry_generations.ScopeId
            OR child.Layer < 0 OR child.NodeKind NOT IN ('Leaf', 'Summary')
            OR (child.ParentNodeId IS NOT NULL AND (parent.NodeId IS NULL
                OR parent.GenerationId IS NOT child.GenerationId OR parent.ScopeKind IS NOT child.ScopeKind
                OR parent.ScopeId IS NOT child.ScopeId OR parent.NodeKind <> 'Summary' OR parent.Layer <= child.Layer))));
