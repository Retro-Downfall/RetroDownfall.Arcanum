CREATE TRIGGER IF NOT EXISTS lexicon_entries_au
AFTER UPDATE ON lexicon_entries
BEGIN
    INSERT INTO lexicon_fts(lexicon_fts, rowid, Name, Type, FactsText)
    VALUES ('delete', old.rowid, old.Name, old.Type, old.FactsText);

    INSERT INTO lexicon_fts(rowid, Name, Type, FactsText)
    VALUES (new.rowid, new.Name, new.Type, new.FactsText);
END;
