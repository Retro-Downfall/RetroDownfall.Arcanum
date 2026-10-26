CREATE INDEX IF NOT EXISTS idx_workspace_file_chunks_tapestry_scope_id ON workspace_file_chunks(WorkspacePath, ChunkId COLLATE BINARY);
