CREATE TRIGGER IF NOT EXISTS "session_attachment_embeddings_quantized_invalidate"
BEFORE UPDATE OF "ChunkId", "Embedding", "Dim" ON "session_attachment_embeddings"
BEGIN
    DELETE FROM "session_attachment_embeddings_quantized" WHERE "ChunkId" = OLD."ChunkId";
END;
