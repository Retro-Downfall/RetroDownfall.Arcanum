CREATE TRIGGER IF NOT EXISTS long_rest_attachment_delete
AFTER DELETE ON saga_memory_attachment_provenance
BEGIN
    UPDATE long_rest_sources SET MutationEpoch = MutationEpoch + 1 WHERE MemoryId = OLD.MemoryId;
END;
