CREATE INDEX IF NOT EXISTS idx_session_attachment_chunks_tapestry_scope_id ON session_attachment_chunks(SessionId, ChunkId COLLATE BINARY);
