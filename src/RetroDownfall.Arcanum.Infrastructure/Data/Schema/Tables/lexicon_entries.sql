CREATE TABLE IF NOT EXISTS lexicon_entries (
    Id TEXT PRIMARY KEY,
    Name TEXT NOT NULL,
    NameNormalized TEXT NOT NULL,
    Type TEXT NOT NULL,
    FactsJson TEXT NOT NULL,
    FactsText TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS IX_lexicon_entries_NameNormalized
ON lexicon_entries(NameNormalized);
