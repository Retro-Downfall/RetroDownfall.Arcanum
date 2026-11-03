CREATE TRIGGER IF NOT EXISTS "tapestry_node_embeddings_quantized_invalidate"
BEFORE UPDATE OF "NodeId", "Embedding", "Dim" ON "tapestry_node_embeddings"
BEGIN
    DELETE FROM "tapestry_node_embeddings_quantized" WHERE "NodeId" = OLD."NodeId";
END;
