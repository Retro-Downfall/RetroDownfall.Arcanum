CREATE TABLE IF NOT EXISTS annal_review_events (
    Sequence INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT CHECK (Sequence > 0), VersionId TEXT NOT NULL REFERENCES annal_versions(VersionId) ON DELETE CASCADE,
    ClaimId TEXT NOT NULL, SubjectStoreCode INTEGER NOT NULL CHECK (SubjectStoreCode IN (1, 2)), SubjectId TEXT NOT NULL,
    OperationCode INTEGER NOT NULL CHECK (OperationCode IN (1, 2, 3)), OriginCode INTEGER NOT NULL CHECK (OriginCode IN (1, 2, 3, 4)),
    ScopeKindCode INTEGER NOT NULL CHECK (ScopeKindCode IN (0, 1, 2, 3)), CampaignId TEXT NULL, SourceSessionId TEXT NULL,
    UNIQUE (VersionId), CHECK ((ScopeKindCode = 2 AND CampaignId IS NOT NULL) OR (ScopeKindCode <> 2 AND CampaignId IS NULL))
);
CREATE INDEX IF NOT EXISTS idx_annal_review_events_global_queue ON annal_review_events(SubjectStoreCode, ScopeKindCode, Sequence DESC) WHERE CampaignId IS NULL;
CREATE INDEX IF NOT EXISTS idx_annal_review_events_campaign_queue ON annal_review_events(SubjectStoreCode, CampaignId, ScopeKindCode, Sequence DESC) WHERE CampaignId IS NOT NULL;
