-- Every existing row becomes global here, with no sweep, which is what keeps today's Lexicon
-- behaviour byte-identical across the upgrade.
ALTER TABLE lexicon_entries ADD COLUMN ScopeCampaignId TEXT NOT NULL DEFAULT '';
