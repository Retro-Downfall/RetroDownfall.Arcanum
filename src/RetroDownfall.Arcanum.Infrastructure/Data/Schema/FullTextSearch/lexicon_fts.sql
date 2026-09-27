-- Triggers and the version-11 projection rebuild index active entries only. The external content
-- table also holds retired entries, so FTS5's unfiltered 'rebuild' command must never be used here.
CREATE VIRTUAL TABLE IF NOT EXISTS lexicon_fts USING fts5(
    Name,
    Type,
    FactsText,
    content='lexicon_entries',
    content_rowid='rowid'
);
