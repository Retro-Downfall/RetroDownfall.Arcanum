INSERT INTO lexicon_fts(rowid, Name, Type, FactsText)
SELECT rowid, Name, Type, FactsText FROM lexicon_entries WHERE RetiredAtUtc IS NULL;
