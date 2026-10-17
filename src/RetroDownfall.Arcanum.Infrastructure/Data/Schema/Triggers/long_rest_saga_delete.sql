CREATE TRIGGER IF NOT EXISTS long_rest_saga_delete
BEFORE DELETE ON saga_memories
BEGIN
    DELETE FROM long_rest_sources WHERE MemoryId = OLD.Id;
END;
