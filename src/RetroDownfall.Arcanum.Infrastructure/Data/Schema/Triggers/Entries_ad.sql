-- Keyed by rowid, not by the UNINDEXED Id column: FTS5 can only satisfy MATCH, rowid and rank
-- constraints, so `WHERE Id = old.Id` scans the entire index once per deleted entry, which makes a
-- session purge or an entry-retention sweep quadratic.
CREATE TRIGGER IF NOT EXISTS Entries_ad AFTER DELETE ON Entries BEGIN
    DELETE FROM Entries_fts WHERE rowid = old.rowid;
END;
