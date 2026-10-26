CREATE INDEX IF NOT EXISTS idx_memory_search_workspace_file_chunks_owner ON workspace_file_chunks(WorkspacePath,IndexedAt DESC,RelativePath ASC,ChunkIndex ASC,ChunkId ASC);
