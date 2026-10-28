CREATE INDEX IF NOT EXISTS idx_tapestry_nodes_scope
    ON tapestry_nodes(ScopeKind, ScopeId, NodeId);
