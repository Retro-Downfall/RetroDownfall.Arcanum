CREATE INDEX IF NOT EXISTS idx_session_attachment_chunks_retrieval_scope
    ON session_attachment_chunks(RetrievalScope, ChunkId, MimeType);
