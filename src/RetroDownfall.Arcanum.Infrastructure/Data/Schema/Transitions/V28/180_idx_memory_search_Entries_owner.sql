CREATE INDEX IF NOT EXISTS idx_memory_search_Entries_owner ON Entries(SessionId,CreatedAt DESC,Sequence DESC,Id DESC);
