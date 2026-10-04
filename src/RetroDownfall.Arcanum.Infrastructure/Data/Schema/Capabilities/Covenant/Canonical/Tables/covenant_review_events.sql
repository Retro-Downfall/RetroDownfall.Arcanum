CREATE TABLE IF NOT EXISTS covenant_review_events (
    Sequence INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT CHECK (Sequence > 0),
    DatasetGeneration BLOB NOT NULL CHECK (length(DatasetGeneration) = 16),
    VersionId TEXT NOT NULL REFERENCES covenant_versions(VersionId) ON DELETE CASCADE,
    EntryId TEXT NOT NULL,
    LaneCode INTEGER NOT NULL CHECK (LaneCode IN (1, 2)),
    OperationCode INTEGER NOT NULL CHECK (OperationCode IN (1, 2)),
    OriginCode INTEGER NOT NULL CHECK (OriginCode IN (1, 2, 3)),
    ScopeCode INTEGER NOT NULL CHECK (ScopeCode IN (1, 2)),
    CampaignId TEXT NULL,
    SourceTurnId TEXT NULL,
    SourceToolCallId TEXT NULL,
    UNIQUE (VersionId),
    CHECK ((ScopeCode = 1 AND CampaignId IS NULL) OR (ScopeCode = 2 AND CampaignId IS NOT NULL))
);

CREATE INDEX IF NOT EXISTS idx_covenant_review_events_global_queue
    ON covenant_review_events(ScopeCode, LaneCode, Sequence DESC) WHERE CampaignId IS NULL;

CREATE INDEX IF NOT EXISTS idx_covenant_review_events_campaign_queue
    ON covenant_review_events(CampaignId, ScopeCode, LaneCode, Sequence DESC) WHERE CampaignId IS NOT NULL;
