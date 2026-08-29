-- The turn snapshot reads one Campaign's live masks through this index, so it leads with CampaignId.
CREATE INDEX IF NOT EXISTS idx_covenant_curation_heads_campaign_masks
    ON covenant_curation_heads(CampaignId, IsMasked, NormalizedKey);
