CREATE TRIGGER IF NOT EXISTS memory_search_Entries_delete AFTER DELETE ON Entries BEGIN
    DELETE FROM memory_search_documents WHERE Corpus=0 AND (SourceId=old.Id);
END;
