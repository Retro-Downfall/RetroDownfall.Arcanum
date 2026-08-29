CREATE UNIQUE INDEX IF NOT EXISTS IX_lexicon_entries_Scope_NameNormalized
ON lexicon_entries(ScopeCampaignId, NameNormalized);
