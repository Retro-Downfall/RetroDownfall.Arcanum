CREATE TRIGGER IF NOT EXISTS memory_search_Sessions_delete AFTER DELETE ON Sessions BEGIN
    DELETE FROM memory_search_documents WHERE Corpus=1 AND (SourceId=old.Id);
END;
