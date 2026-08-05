CREATE TABLE IF NOT EXISTS saga_extraction_watermarks (
    SessionId TEXT PRIMARY KEY,
    LastExtractedEntryCreatedAt TEXT NOT NULL
);
