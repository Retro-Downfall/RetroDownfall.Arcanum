CREATE TRIGGER IF NOT EXISTS "saga_memory_embeddings_quantized_invalidate"
BEFORE UPDATE OF "MemoryId", "Embedding", "Dim" ON "saga_memory_embeddings"
BEGIN
    DELETE FROM "saga_memory_embeddings_quantized" WHERE "MemoryId" = OLD."MemoryId";
END;
