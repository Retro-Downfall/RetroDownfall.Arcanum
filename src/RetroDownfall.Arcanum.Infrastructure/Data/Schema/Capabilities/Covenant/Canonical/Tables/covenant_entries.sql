CREATE TABLE IF NOT EXISTS covenant_entries (
    EntryId TEXT NOT NULL PRIMARY KEY,
    ScopeCode INTEGER NOT NULL CHECK (ScopeCode IN (1, 2)),
    CampaignId TEXT NULL,
    AuthoredKey TEXT NOT NULL,
    NormalizedKey TEXT NOT NULL CHECK (length(NormalizedKey) BETWEEN 1 AND 128),
    CreatedAtUtc TEXT NOT NULL,
    CHECK ((ScopeCode = 1 AND CampaignId IS NULL) OR (ScopeCode = 2 AND CampaignId IS NOT NULL))
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_covenant_entries_global_key
    ON covenant_entries(NormalizedKey) WHERE CampaignId IS NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_covenant_entries_campaign_key
    ON covenant_entries(CampaignId, NormalizedKey) WHERE CampaignId IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_covenant_entries_campaign
    ON covenant_entries(CampaignId, NormalizedKey);
