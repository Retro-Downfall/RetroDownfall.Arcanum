CREATE TRIGGER IF NOT EXISTS "workspace_file_embeddings_quantized_invalidate"
BEFORE UPDATE OF "ChunkId", "Embedding", "Dim" ON "workspace_file_embeddings"
BEGIN
    DELETE FROM "workspace_file_embeddings_quantized" WHERE "ChunkId" = OLD."ChunkId";
END;
