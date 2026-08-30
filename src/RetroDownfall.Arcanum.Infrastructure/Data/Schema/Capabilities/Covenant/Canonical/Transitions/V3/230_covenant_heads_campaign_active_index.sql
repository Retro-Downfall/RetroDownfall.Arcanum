CREATE INDEX IF NOT EXISTS idx_covenant_heads_campaign_active
    ON covenant_heads(CampaignId, NormalizedKey, LaneCode, CurrentOperationCode) WHERE CampaignId IS NOT NULL;
