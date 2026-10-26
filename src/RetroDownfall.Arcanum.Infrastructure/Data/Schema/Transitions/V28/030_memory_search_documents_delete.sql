CREATE TRIGGER IF NOT EXISTS memory_search_documents_delete BEFORE DELETE ON memory_search_documents BEGIN
    INSERT INTO memory_search_fts(memory_search_fts,rowid,Content,AuxiliaryOne,AuxiliaryTwo)
    VALUES('delete',old.SearchId,old.Content,old.AuxiliaryOne,old.AuxiliaryTwo);
END;
