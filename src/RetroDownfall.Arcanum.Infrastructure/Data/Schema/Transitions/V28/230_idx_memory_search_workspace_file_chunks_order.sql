CREATE INDEX IF NOT EXISTS idx_memory_search_workspace_file_chunks_order ON workspace_file_chunks(IndexedAt DESC,RelativePath ASC,ChunkIndex ASC,ChunkId ASC);
