CREATE TRIGGER IF NOT EXISTS memory_search_session_attachment_chunks_delete AFTER DELETE ON session_attachment_chunks BEGIN
    DELETE FROM memory_search_documents WHERE Corpus=2 AND (SourceId=old.ChunkId);
END;
