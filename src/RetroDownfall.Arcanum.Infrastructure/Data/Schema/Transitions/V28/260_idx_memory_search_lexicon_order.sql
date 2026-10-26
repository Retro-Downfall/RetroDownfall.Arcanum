CREATE INDEX IF NOT EXISTS idx_memory_search_lexicon_order ON lexicon_entries(Name COLLATE NOCASE,ScopeCampaignId,Id);
