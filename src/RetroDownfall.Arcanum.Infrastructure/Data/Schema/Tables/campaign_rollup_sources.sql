-- The exact immutable source manifest accompanies every rollup publication revision.
CREATE TABLE IF NOT EXISTS campaign_rollup_sources (
    RollupArtifactId TEXT NOT NULL,
    SessionId TEXT NOT NULL,
    CampaignId TEXT NOT NULL,
    RollupRevision INTEGER NOT NULL CHECK (RollupRevision > 0),
    ContributionArtifactId TEXT NOT NULL,
    ContributionRevision INTEGER NOT NULL CHECK (ContributionRevision > 0),
    ContentDigest BLOB NOT NULL CHECK (length(ContentDigest) = 32),
    SensitivityDigest BLOB NOT NULL CHECK (length(SensitivityDigest) = 32),
    SummarizedThroughSequence INTEGER NOT NULL CHECK (SummarizedThroughSequence >= 0),
    PRIMARY KEY (RollupArtifactId, SessionId),
    FOREIGN KEY (RollupArtifactId, CampaignId, RollupRevision) REFERENCES campaign_rollup_artifacts(ArtifactId, CampaignId, Revision) ON DELETE CASCADE,
    FOREIGN KEY (ContributionArtifactId, SessionId, CampaignId, ContributionRevision) REFERENCES campaign_contribution_artifacts(ArtifactId, SessionId, CampaignId, Revision) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_campaign_rollup_sources_contribution ON campaign_rollup_sources(ContributionArtifactId);
