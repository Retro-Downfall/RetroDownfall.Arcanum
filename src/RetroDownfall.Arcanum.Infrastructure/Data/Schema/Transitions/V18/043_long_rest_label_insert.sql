CREATE TRIGGER IF NOT EXISTS long_rest_label_insert
AFTER INSERT ON artifact_sensitivity
WHEN NEW.ArtifactKindCode = 6
BEGIN
    UPDATE long_rest_sources SET MutationEpoch = MutationEpoch + 1
    WHERE lower(replace(MemoryId, '-', '')) = lower(replace(NEW.ArtifactId, '-', ''));
END;
