CREATE VIRTUAL TABLE IF NOT EXISTS Entries_fts USING fts5(
    Id UNINDEXED,
    SessionId UNINDEXED,
    Role UNINDEXED,
    Content
);
