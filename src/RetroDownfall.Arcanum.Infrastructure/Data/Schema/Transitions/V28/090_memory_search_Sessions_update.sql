CREATE TRIGGER IF NOT EXISTS memory_search_Sessions_update AFTER UPDATE ON Sessions BEGIN
    DELETE FROM memory_search_documents WHERE Corpus=1 AND (SourceId=new.Id OR SourceId=old.Id);
    INSERT INTO memory_search_documents(Corpus, SourceId, Owner, SortAt, SortNumber, SortPath, ConflictOwner, ConflictGeneration, Content, AuxiliaryOne, AuxiliaryTwo) SELECT 1,new.Id,new.Id,new.UpdatedAt,0,'','','',lower(new.Summary),lower(''),lower('') WHERE new.Summary IS NOT NULL;
END;
