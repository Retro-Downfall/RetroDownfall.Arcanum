CREATE VIRTUAL TABLE IF NOT EXISTS saga_memory_embeddings_vec USING vec0(
    MemoryId TEXT PRIMARY KEY,
    Embedding FLOAT[{{EmbeddingDimensions}}] distance_metric=cosine
);
