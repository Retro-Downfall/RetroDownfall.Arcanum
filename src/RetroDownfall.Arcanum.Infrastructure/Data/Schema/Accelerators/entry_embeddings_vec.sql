CREATE VIRTUAL TABLE IF NOT EXISTS entry_embeddings_vec USING vec0(
    EntryId TEXT PRIMARY KEY,
    Embedding FLOAT[{{EmbeddingDimensions}}] distance_metric=cosine
);
