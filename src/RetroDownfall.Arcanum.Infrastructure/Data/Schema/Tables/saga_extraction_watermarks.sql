-- LastExtractedEntrySequence is laid out exactly as SQLite splices an added column into the stored
-- table declaration. It begins NULL only on an inherited row while the version-7 backfill is
-- pending; the table's insert and update guards refuse NULL and negative values on every later write.
-- A fresh installation and one evolved through ALTER must normalize to the same definition, including
-- the space SQLite places before the inserted comma.
CREATE TABLE IF NOT EXISTS saga_extraction_watermarks (
    SessionId TEXT PRIMARY KEY,
    LastExtractedEntryCreatedAt TEXT NOT NULL
, LastExtractedEntrySequence INTEGER);

-- Retention compares SessionId normalized - lower(replace(SessionId, '-', '')) - when a Session is
-- deleted, both in the delete itself and in the post-commit count that proves it, and SQLite cannot
-- answer a function-wrapped column from an ordinary column index. The expression here has to stay
-- character for character the shape the predicate has, because that is how SQLite decides the index
-- applies.
CREATE INDEX IF NOT EXISTS IX_saga_extraction_watermarks_SessionId_Norm
    ON saga_extraction_watermarks (lower(replace(SessionId, '-', '')));
