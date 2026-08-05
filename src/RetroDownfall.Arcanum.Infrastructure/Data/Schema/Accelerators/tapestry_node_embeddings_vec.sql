CREATE VIRTUAL TABLE IF NOT EXISTS tapestry_node_embeddings_vec USING vec0(
    NodeId TEXT PRIMARY KEY,
    Embedding FLOAT[{{EmbeddingDimensions}}] distance_metric=cosine
);
