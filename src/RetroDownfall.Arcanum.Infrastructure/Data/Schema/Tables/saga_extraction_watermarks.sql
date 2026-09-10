-- LastExtractedEntrySequence is laid out exactly as SQLite splices an added column into the stored
-- table declaration. It begins NULL only on an inherited row while the version-7 backfill is
-- pending; the table's insert and update guards refuse NULL and negative values on every later write.
-- A fresh installation and one evolved through ALTER must normalize to the same definition, including
-- the space SQLite places before the inserted comma.
CREATE TABLE IF NOT EXISTS saga_extraction_watermarks (
    SessionId TEXT PRIMARY KEY,
    LastExtractedEntryCreatedAt TEXT NOT NULL
, LastExtractedEntrySequence INTEGER);
