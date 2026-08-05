CREATE VIRTUAL TABLE IF NOT EXISTS workspace_file_embeddings_vec USING vec0(
    ChunkId TEXT PRIMARY KEY,
    Embedding FLOAT[{{EmbeddingDimensions}}] distance_metric=cosine
);
