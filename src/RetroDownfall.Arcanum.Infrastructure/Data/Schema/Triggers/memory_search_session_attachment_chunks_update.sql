CREATE TRIGGER IF NOT EXISTS memory_search_session_attachment_chunks_update AFTER UPDATE ON session_attachment_chunks BEGIN
    DELETE FROM memory_search_documents WHERE Corpus=2 AND (SourceId=new.ChunkId OR SourceId=old.ChunkId OR (ConflictOwner=new.AttachmentId AND ConflictGeneration=new.GenerationId AND SortNumber=new.ChunkIndex));
    INSERT INTO memory_search_documents(Corpus, SourceId, Owner, SortAt, SortNumber, SortPath, ConflictOwner, ConflictGeneration, Content, AuxiliaryOne, AuxiliaryTwo) SELECT 2,new.ChunkId,new.SessionId,new.IndexedAt,new.ChunkIndex,'',new.AttachmentId,new.GenerationId,lower(new.Content),lower(new.OriginalFileName),lower(new.LogicalKey);
END;
