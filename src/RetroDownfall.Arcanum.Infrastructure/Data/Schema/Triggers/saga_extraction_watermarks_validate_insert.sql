-- NULL identifies only an inherited row whose version-7 backfill has not committed yet. Once this
-- guard exists, every new cursor must name a real non-negative resume position.
CREATE TRIGGER IF NOT EXISTS saga_extraction_watermarks_validate_insert
BEFORE INSERT ON saga_extraction_watermarks
WHEN NEW.LastExtractedEntrySequence IS NULL OR NEW.LastExtractedEntrySequence < 0
BEGIN
    SELECT RAISE(ABORT, 'A Saga extraction cursor requires a non-negative entry sequence.');
END;
