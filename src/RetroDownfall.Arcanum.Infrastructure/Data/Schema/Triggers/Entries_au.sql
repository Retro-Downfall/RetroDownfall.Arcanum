-- Keyed by rowid for the same reason as Entries_ad: an equality on the UNINDEXED Id column costs a
-- full scan of Entries_fts on every entry update.
CREATE TRIGGER IF NOT EXISTS Entries_au AFTER UPDATE ON Entries BEGIN
    DELETE FROM Entries_fts WHERE rowid = old.rowid;

    INSERT INTO Entries_fts(rowid, Id, SessionId, Role, Content)
    VALUES (new.rowid, new.Id, new.SessionId, new.Role, new.Content);
END;
