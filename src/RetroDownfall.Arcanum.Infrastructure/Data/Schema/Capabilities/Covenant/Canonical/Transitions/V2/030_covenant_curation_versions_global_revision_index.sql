CREATE UNIQUE INDEX IF NOT EXISTS ux_covenant_curation_versions_global_revision
    ON covenant_curation_versions(NormalizedKey, LaneCode, KeyEpoch, Revision) WHERE CampaignId IS NULL;
