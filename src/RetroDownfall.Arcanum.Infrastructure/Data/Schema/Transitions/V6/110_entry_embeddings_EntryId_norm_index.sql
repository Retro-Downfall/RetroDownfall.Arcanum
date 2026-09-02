-- Same rule as the statement beside it: this text and Tables/entry_embeddings.sql's have to agree.
CREATE INDEX IF NOT EXISTS IX_entry_embeddings_EntryId_Norm
  ON entry_embeddings (lower(replace(EntryId, '-', '')));
