CREATE TABLE IF NOT EXISTS tapestry_nodes (
    NodeId TEXT PRIMARY KEY,
    GenerationId TEXT NOT NULL,
    ScopeKind TEXT NOT NULL,
    ScopeId TEXT NOT NULL,
    Layer INTEGER NOT NULL,
    ParentScopeKey TEXT NOT NULL,
    NodeKind TEXT NOT NULL,
    ParentNodeId TEXT,
    SourceKind TEXT,
    SourceId TEXT,
    SourceLabel TEXT NOT NULL,
    Content TEXT,
    ContentHash TEXT NOT NULL,
    ChildMembershipHash TEXT,
    DescendantLeafCount INTEGER NOT NULL DEFAULT 1,
    ClusterOrdinal INTEGER NOT NULL DEFAULT 0,
    PartitionReason TEXT NOT NULL DEFAULT 'None',
    EmbeddingDimension INTEGER NOT NULL,
    CreatedAt TEXT NOT NULL,
    FOREIGN KEY(GenerationId) REFERENCES tapestry_generations(GenerationId) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_tapestry_nodes_generation
    ON tapestry_nodes(GenerationId, Layer);
CREATE INDEX IF NOT EXISTS idx_tapestry_nodes_parent_scope
    ON tapestry_nodes(ParentScopeKey);
CREATE INDEX IF NOT EXISTS idx_tapestry_nodes_parent
    ON tapestry_nodes(ParentNodeId);
CREATE INDEX IF NOT EXISTS idx_tapestry_nodes_membership
    ON tapestry_nodes(GenerationId, ChildMembershipHash);
