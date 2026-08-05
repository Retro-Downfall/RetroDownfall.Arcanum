CREATE TABLE IF NOT EXISTS workspace_file_embeddings (
    ChunkId TEXT PRIMARY KEY,
    Embedding BLOB NOT NULL,
    Dim INTEGER NOT NULL
);
