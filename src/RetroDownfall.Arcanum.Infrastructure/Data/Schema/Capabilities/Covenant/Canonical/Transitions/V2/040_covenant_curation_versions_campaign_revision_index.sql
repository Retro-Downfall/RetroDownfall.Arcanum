CREATE UNIQUE INDEX IF NOT EXISTS ux_covenant_curation_versions_campaign_revision
    ON covenant_curation_versions(CampaignId, NormalizedKey, LaneCode, KeyEpoch, Revision) WHERE CampaignId IS NOT NULL;
