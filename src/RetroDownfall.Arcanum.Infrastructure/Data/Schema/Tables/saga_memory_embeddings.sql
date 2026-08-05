CREATE TABLE IF NOT EXISTS saga_memory_embeddings (
    MemoryId TEXT PRIMARY KEY,
    Embedding BLOB NOT NULL,
    Dim INTEGER NOT NULL
);
