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
