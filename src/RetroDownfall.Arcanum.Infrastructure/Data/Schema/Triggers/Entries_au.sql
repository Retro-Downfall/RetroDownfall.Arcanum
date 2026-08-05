CREATE TRIGGER IF NOT EXISTS Entries_au AFTER UPDATE ON Entries BEGIN
    DELETE FROM Entries_fts WHERE Id = old.Id;
    INSERT INTO Entries_fts(Id, SessionId, Role, Content)
    VALUES (new.Id, new.SessionId, new.Role, new.Content);
END;
