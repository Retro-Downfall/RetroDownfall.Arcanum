-- Entries_fts is a standalone FTS5 table, so its rowid is the only key FTS5 can resolve without
-- scanning the whole index (Id/SessionId/Role are UNINDEXED stored columns). Mirroring the entry's
-- own rowid keeps the delete and update triggers below a rowid lookup rather than a full scan.
CREATE TRIGGER IF NOT EXISTS Entries_ai AFTER INSERT ON Entries BEGIN
    INSERT INTO Entries_fts(rowid, Id, SessionId, Role, Content)
    VALUES (new.rowid, new.Id, new.SessionId, new.Role, new.Content);
END;
