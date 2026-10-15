CREATE TABLE IF NOT EXISTS long_rest_discovery_edges (
    GenerationId TEXT NOT NULL,
    ParentNodeId TEXT NOT NULL,
    Ordinal INTEGER NOT NULL CHECK (Ordinal BETWEEN 1 AND 16),
    ChildNodeId TEXT NOT NULL,
    PRIMARY KEY (GenerationId, ParentNodeId, Ordinal),
    UNIQUE (GenerationId, ChildNodeId),
    FOREIGN KEY (GenerationId, ParentNodeId) REFERENCES long_rest_discovery_nodes(GenerationId, NodeId) ON DELETE CASCADE,
    FOREIGN KEY (GenerationId, ChildNodeId) REFERENCES long_rest_discovery_nodes(GenerationId, NodeId) ON DELETE CASCADE,
    CHECK (ParentNodeId <> ChildNodeId)
);

-- The label guard also proves that no edge still names the label's node, as parent or as child, and it does so by the normalized
-- identity: lower(replace(col, '-', '')). Each side needs its own index in that exact shape, or the OR is a scan of every edge.
CREATE INDEX IF NOT EXISTS idx_long_rest_edges_parent_norm
    ON long_rest_discovery_edges(lower(replace(ParentNodeId, '-', '')));

CREATE INDEX IF NOT EXISTS idx_long_rest_edges_child_norm
    ON long_rest_discovery_edges(lower(replace(ChildNodeId, '-', '')));
