CREATE TRIGGER IF NOT EXISTS lexicon_entries_ai
AFTER INSERT ON lexicon_entries
BEGIN
    INSERT INTO lexicon_fts(rowid, Name, Type, FactsText)
    VALUES (new.rowid, new.Name, new.Type, new.FactsText);
END;
