CREATE INDEX IF NOT EXISTS idx_covenant_heads_global_active
    ON covenant_heads(NormalizedKey, LaneCode, CurrentOperationCode) WHERE CampaignId IS NULL;
