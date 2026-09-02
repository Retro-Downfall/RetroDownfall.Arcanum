CREATE TABLE IF NOT EXISTS entry_embeddings (
    EntryId TEXT PRIMARY KEY,
    Embedding BLOB NOT NULL,
    Dim INTEGER NOT NULL
);

-- The prune deletes from this table by lower(replace(EntryId, '-', '')) once per candidate entry, and
-- reconciles the same way afterwards. The primary key on EntryId cannot serve a wrapped column, so
-- both were full scans; this index is the shape those statements ask for.
CREATE INDEX IF NOT EXISTS IX_entry_embeddings_EntryId_Norm
  ON entry_embeddings (lower(replace(EntryId, '-', '')));
