CREATE TRIGGER IF NOT EXISTS memory_search_Entries_insert AFTER INSERT ON Entries BEGIN
    DELETE FROM memory_search_documents WHERE Corpus=0 AND SourceId=new.Id;
    DELETE FROM memory_search_documents WHERE Corpus=0 AND Owner=new.SessionId AND SortNumber=new.Sequence;
    INSERT INTO memory_search_documents(Corpus, SourceId, Owner, SortAt, SortNumber, SortPath, ConflictOwner, ConflictGeneration, Content, AuxiliaryOne, AuxiliaryTwo) SELECT 0,new.Id,new.SessionId,new.CreatedAt,new.Sequence,'','','',lower(new.Content),lower(''),lower('');
END;
