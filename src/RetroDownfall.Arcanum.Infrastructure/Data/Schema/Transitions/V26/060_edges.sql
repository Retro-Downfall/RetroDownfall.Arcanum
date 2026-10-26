CREATE TABLE IF NOT EXISTS tapestry_edges (
    GenerationId TEXT NOT NULL,
    ParentNodeId TEXT NOT NULL,
    ChildNodeId TEXT NOT NULL,
    GlobalPosterior REAL CHECK (GlobalPosterior >= 0 AND GlobalPosterior <= 1),
    LocalPosterior REAL CHECK (LocalPosterior >= 0 AND LocalPosterior <= 1),
    MembershipReason TEXT NOT NULL,
    PRIMARY KEY (GenerationId, ParentNodeId, ChildNodeId),
    FOREIGN KEY (GenerationId, ParentNodeId) REFERENCES tapestry_nodes(GenerationId, NodeId) ON DELETE CASCADE,
    FOREIGN KEY (GenerationId, ChildNodeId) REFERENCES tapestry_nodes(GenerationId, NodeId) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_tapestry_edges_child ON tapestry_edges(GenerationId, ChildNodeId, ParentNodeId);
