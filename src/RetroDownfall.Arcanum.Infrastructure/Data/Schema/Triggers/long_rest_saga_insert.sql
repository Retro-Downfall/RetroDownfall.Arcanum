CREATE TRIGGER IF NOT EXISTS long_rest_saga_insert
AFTER INSERT ON saga_memories
BEGIN
    DELETE FROM long_rest_sources WHERE MemoryId = NEW.Id;
    INSERT INTO long_rest_sources (MemoryId, MutationEpoch) VALUES (NEW.Id, 1);
END;
