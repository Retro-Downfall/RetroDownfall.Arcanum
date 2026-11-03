CREATE TRIGGER IF NOT EXISTS "entry_embeddings_quantized_invalidate"
BEFORE UPDATE OF "EntryId", "Embedding", "Dim" ON "entry_embeddings"
BEGIN
    DELETE FROM "entry_embeddings_quantized" WHERE "EntryId" = OLD."EntryId";
END;
