CREATE UNIQUE INDEX IF NOT EXISTS ux_covenant_curation_heads_campaign_subject
    ON covenant_curation_heads(CampaignId, NormalizedKey, LaneCode, KeyEpoch) WHERE CampaignId IS NOT NULL;
