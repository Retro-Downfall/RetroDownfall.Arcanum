CREATE INDEX IF NOT EXISTS idx_memory_search_session_attachment_chunks_order ON session_attachment_chunks(IndexedAt DESC,ChunkIndex ASC,ChunkId ASC);
