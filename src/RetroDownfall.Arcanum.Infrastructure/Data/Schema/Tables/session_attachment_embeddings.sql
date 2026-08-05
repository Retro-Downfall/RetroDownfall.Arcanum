CREATE TABLE IF NOT EXISTS session_attachment_embeddings (
    ChunkId TEXT PRIMARY KEY,
    Embedding BLOB NOT NULL,
    Dim INTEGER NOT NULL,
    FOREIGN KEY(ChunkId) REFERENCES session_attachment_chunks(ChunkId) ON DELETE CASCADE
);
