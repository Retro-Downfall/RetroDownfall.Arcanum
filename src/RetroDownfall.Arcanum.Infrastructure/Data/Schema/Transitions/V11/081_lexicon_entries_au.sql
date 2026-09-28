CREATE TRIGGER IF NOT EXISTS lexicon_entries_au
AFTER UPDATE ON lexicon_entries
BEGIN
    INSERT INTO lexicon_fts(lexicon_fts, rowid, Name, Type, FactsText)
    SELECT 'delete', old.rowid, old.Name, old.Type, old.FactsText
    WHERE old.RetiredAtUtc IS NULL;

    INSERT INTO lexicon_fts(rowid, Name, Type, FactsText)
    SELECT new.rowid, new.Name, new.Type, new.FactsText
    WHERE new.RetiredAtUtc IS NULL;
END;
