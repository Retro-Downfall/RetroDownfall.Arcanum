-- A NULL inside a SQLite primary key does not enforce uniqueness, so the subject's identity is two
-- partial unique indexes, exactly as covenant_entries keys its own nullable Campaign.
CREATE UNIQUE INDEX IF NOT EXISTS ux_covenant_curation_heads_global_subject
    ON covenant_curation_heads(NormalizedKey, LaneCode, KeyEpoch) WHERE CampaignId IS NULL;
