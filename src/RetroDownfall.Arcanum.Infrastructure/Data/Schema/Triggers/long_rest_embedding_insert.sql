CREATE TRIGGER IF NOT EXISTS long_rest_embedding_insert
AFTER INSERT ON saga_memory_embeddings
BEGIN
    UPDATE long_rest_sources SET MutationEpoch = MutationEpoch + 1 WHERE MemoryId = NEW.MemoryId;
END;
