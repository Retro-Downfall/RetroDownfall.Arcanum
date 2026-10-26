CREATE TRIGGER IF NOT EXISTS memory_search_workspace_file_chunks_delete AFTER DELETE ON workspace_file_chunks BEGIN
    DELETE FROM memory_search_documents WHERE Corpus=3 AND (SourceId=old.ChunkId);
END;
