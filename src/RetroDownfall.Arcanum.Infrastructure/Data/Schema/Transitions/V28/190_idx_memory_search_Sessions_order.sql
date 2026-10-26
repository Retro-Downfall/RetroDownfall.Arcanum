CREATE INDEX IF NOT EXISTS idx_memory_search_Sessions_order ON Sessions(UpdatedAt DESC,Id DESC) WHERE Summary IS NOT NULL;
