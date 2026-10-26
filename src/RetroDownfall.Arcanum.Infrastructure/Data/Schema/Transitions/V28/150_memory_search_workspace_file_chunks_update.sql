CREATE TRIGGER IF NOT EXISTS memory_search_workspace_file_chunks_update AFTER UPDATE ON workspace_file_chunks BEGIN
    DELETE FROM memory_search_documents WHERE Corpus=3 AND (SourceId=new.ChunkId OR SourceId=old.ChunkId);
    INSERT INTO memory_search_documents(Corpus, SourceId, Owner, SortAt, SortNumber, SortPath, ConflictOwner, ConflictGeneration, Content, AuxiliaryOne, AuxiliaryTwo) SELECT 3,new.ChunkId,new.WorkspacePath,new.IndexedAt,new.ChunkIndex,new.RelativePath,'','',lower(new.Content),lower(new.RelativePath),lower('');
END;
