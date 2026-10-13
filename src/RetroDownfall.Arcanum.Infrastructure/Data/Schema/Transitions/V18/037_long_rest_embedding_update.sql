CREATE TRIGGER IF NOT EXISTS long_rest_embedding_update
AFTER UPDATE ON saga_memory_embeddings
BEGIN
    UPDATE long_rest_sources SET MutationEpoch = MutationEpoch + 1 WHERE MemoryId IN (OLD.MemoryId, NEW.MemoryId);
END;
