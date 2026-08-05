CREATE VIRTUAL TABLE IF NOT EXISTS lexicon_fts USING fts5(
    Name,
    Type,
    FactsText,
    content='lexicon_entries',
    content_rowid='rowid'
);
