CREATE INDEX IF NOT EXISTS idx_memory_search_session_attachment_chunks_owner ON session_attachment_chunks(SessionId,IndexedAt DESC,ChunkIndex ASC,ChunkId ASC);
