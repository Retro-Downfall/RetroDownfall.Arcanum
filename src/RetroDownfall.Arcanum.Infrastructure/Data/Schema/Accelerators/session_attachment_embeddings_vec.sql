CREATE VIRTUAL TABLE IF NOT EXISTS session_attachment_embeddings_vec USING vec0(
    ChunkId TEXT PRIMARY KEY,
    Embedding FLOAT[{{EmbeddingDimensions}}] distance_metric=cosine
);
