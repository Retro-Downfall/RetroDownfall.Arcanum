-- The column definition is copied verbatim into the stored CREATE TABLE statement, so it has to
-- match the tail of Tables/saga_extraction_watermarks.sql character for character.
ALTER TABLE saga_extraction_watermarks ADD COLUMN LastExtractedEntrySequence INTEGER;
