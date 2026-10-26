CREATE TRIGGER IF NOT EXISTS memory_search_documents_insert AFTER INSERT ON memory_search_documents BEGIN
    INSERT INTO memory_search_fts(rowid,Content,AuxiliaryOne,AuxiliaryTwo)
    VALUES(new.SearchId,new.Content,new.AuxiliaryOne,new.AuxiliaryTwo);
END;
