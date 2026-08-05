CREATE TRIGGER IF NOT EXISTS lexicon_entries_ad
AFTER DELETE ON lexicon_entries
BEGIN
    INSERT INTO lexicon_fts(lexicon_fts, rowid, Name, Type, FactsText)
    VALUES ('delete', old.rowid, old.Name, old.Type, old.FactsText);
END;
