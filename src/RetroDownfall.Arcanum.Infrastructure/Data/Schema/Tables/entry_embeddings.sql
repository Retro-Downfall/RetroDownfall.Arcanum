CREATE TABLE IF NOT EXISTS entry_embeddings (
    EntryId TEXT PRIMARY KEY,
    Embedding BLOB NOT NULL,
    Dim INTEGER NOT NULL
);
