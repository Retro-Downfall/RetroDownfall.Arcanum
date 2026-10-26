CREATE INDEX IF NOT EXISTS idx_memory_search_Entries_order ON Entries(CreatedAt DESC,Sequence DESC,Id DESC);
