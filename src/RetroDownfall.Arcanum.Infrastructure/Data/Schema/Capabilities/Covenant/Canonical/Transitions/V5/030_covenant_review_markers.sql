CREATE TABLE IF NOT EXISTS covenant_review_markers (
    DatasetGeneration BLOB NOT NULL CHECK (length(DatasetGeneration) = 16), ScopeCode INTEGER NOT NULL CHECK (ScopeCode IN (1, 2)),
    CampaignId TEXT NULL, LaneCode INTEGER NOT NULL CHECK (LaneCode IN (1, 2)), ReviewedThroughSequence INTEGER NOT NULL CHECK (ReviewedThroughSequence >= 0),
    Revision INTEGER NOT NULL CHECK (Revision > 0), CHECK ((ScopeCode = 1 AND CampaignId IS NULL) OR (ScopeCode = 2 AND CampaignId IS NOT NULL))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_covenant_review_markers_global_scope ON covenant_review_markers(DatasetGeneration, ScopeCode, LaneCode) WHERE CampaignId IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_covenant_review_markers_campaign_scope ON covenant_review_markers(DatasetGeneration, ScopeCode, CampaignId, LaneCode) WHERE CampaignId IS NOT NULL;
