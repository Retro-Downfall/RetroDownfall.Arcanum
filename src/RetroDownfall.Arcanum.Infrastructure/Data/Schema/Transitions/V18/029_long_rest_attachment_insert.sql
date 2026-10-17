CREATE TRIGGER IF NOT EXISTS long_rest_attachment_insert
AFTER INSERT ON saga_memory_attachment_provenance
BEGIN
    UPDATE long_rest_sources SET MutationEpoch = MutationEpoch + 1 WHERE MemoryId = NEW.MemoryId;
END;
