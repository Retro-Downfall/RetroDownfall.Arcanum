CREATE TRIGGER IF NOT EXISTS long_rest_label_delete
AFTER DELETE ON artifact_sensitivity
WHEN OLD.ArtifactKindCode = 6
BEGIN
    UPDATE long_rest_sources SET MutationEpoch = MutationEpoch + 1
    WHERE lower(replace(MemoryId, '-', '')) = lower(replace(OLD.ArtifactId, '-', ''));
END;
