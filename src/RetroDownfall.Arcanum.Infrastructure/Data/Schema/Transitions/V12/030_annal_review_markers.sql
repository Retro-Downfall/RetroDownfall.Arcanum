CREATE TABLE IF NOT EXISTS annal_review_markers (
    SubjectStoreCode INTEGER NOT NULL CHECK (SubjectStoreCode IN (1, 2)), ScopeKindCode INTEGER NOT NULL CHECK (ScopeKindCode IN (0, 1, 2, 3)),
    CampaignId TEXT NULL, MarkerGeneration BLOB NOT NULL CHECK (length(MarkerGeneration) = 16 AND MarkerGeneration <> zeroblob(16)),
    ReviewedThroughSequence INTEGER NOT NULL CHECK (ReviewedThroughSequence >= 0), Revision INTEGER NOT NULL CHECK (Revision > 0),
    CHECK ((ScopeKindCode = 2 AND CampaignId IS NOT NULL) OR (ScopeKindCode <> 2 AND CampaignId IS NULL))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_annal_review_markers_global_scope ON annal_review_markers(SubjectStoreCode, ScopeKindCode) WHERE CampaignId IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_annal_review_markers_campaign_scope ON annal_review_markers(SubjectStoreCode, ScopeKindCode, CampaignId) WHERE CampaignId IS NOT NULL;
