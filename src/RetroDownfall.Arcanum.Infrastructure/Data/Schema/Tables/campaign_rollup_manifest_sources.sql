-- Each Session is stored once in a lineage; summary roots select immutable prefixes.
CREATE TABLE IF NOT EXISTS campaign_rollup_manifest_sources (
    ManifestId TEXT NOT NULL,
    LineageId TEXT NOT NULL,
    CampaignId TEXT NOT NULL,
    SessionId TEXT NOT NULL,
    ContributionArtifactId TEXT NOT NULL,
    ContributionRevision INTEGER NOT NULL CHECK (ContributionRevision > 0),
    ContentDigest BLOB NOT NULL CHECK (length(ContentDigest) = 32),
    SensitivityDigest BLOB NOT NULL CHECK (length(SensitivityDigest) = 32),
    SummarizedThroughSequence INTEGER NOT NULL CHECK (SummarizedThroughSequence >= 0),
    PRIMARY KEY (ManifestId, SessionId),
    UNIQUE (LineageId, SessionId),
    FOREIGN KEY (ManifestId, LineageId, CampaignId) REFERENCES campaign_rollup_manifests(ManifestId, LineageId, CampaignId) ON DELETE CASCADE,
    FOREIGN KEY (ContributionArtifactId, SessionId, CampaignId, ContributionRevision) REFERENCES campaign_contribution_artifacts(ArtifactId, SessionId, CampaignId, Revision) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_campaign_rollup_manifest_sources_session ON campaign_rollup_manifest_sources(SessionId, ManifestId);
CREATE INDEX IF NOT EXISTS idx_campaign_rollup_manifest_sources_contribution ON campaign_rollup_manifest_sources(ContributionArtifactId, ManifestId);
