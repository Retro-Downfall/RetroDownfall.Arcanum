CREATE TABLE IF NOT EXISTS tapestry_node_embeddings (
    NodeId TEXT PRIMARY KEY,
    Embedding BLOB NOT NULL,
    Dim INTEGER NOT NULL,
    FOREIGN KEY(NodeId) REFERENCES tapestry_nodes(NodeId) ON DELETE CASCADE
);
