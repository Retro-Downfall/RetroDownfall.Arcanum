-- The backfill moves NULL to a resolved value; no writer may move a resolved cursor back into the
-- transition-only state or into a negative sequence.
CREATE TRIGGER IF NOT EXISTS saga_extraction_watermarks_validate_update
BEFORE UPDATE OF LastExtractedEntrySequence ON saga_extraction_watermarks
WHEN NEW.LastExtractedEntrySequence IS NULL OR NEW.LastExtractedEntrySequence < 0
BEGIN
    SELECT RAISE(ABORT, 'A Saga extraction cursor requires a non-negative entry sequence.');
END;
