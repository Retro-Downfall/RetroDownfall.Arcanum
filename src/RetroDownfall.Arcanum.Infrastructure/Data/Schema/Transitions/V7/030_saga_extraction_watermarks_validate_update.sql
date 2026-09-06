-- Kept verbatim with Triggers/saga_extraction_watermarks_validate_update.sql so evolved and fresh
-- installations publish the same trigger definition.
CREATE TRIGGER IF NOT EXISTS saga_extraction_watermarks_validate_update
BEFORE UPDATE OF LastExtractedEntrySequence ON saga_extraction_watermarks
WHEN NEW.LastExtractedEntrySequence IS NULL OR NEW.LastExtractedEntrySequence < 0
BEGIN
    SELECT RAISE(ABORT, 'A Saga extraction cursor requires a non-negative entry sequence.');
END;
