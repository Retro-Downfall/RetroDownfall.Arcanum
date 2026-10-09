-- A contribution summarizes only this Session's native sequence tail.
CREATE TABLE IF NOT EXISTS campaign_contribution_artifacts (
    ArtifactId TEXT NOT NULL PRIMARY KEY,
    CampaignId TEXT NOT NULL REFERENCES "Campaigns" ("Id") ON DELETE CASCADE,
    SessionId TEXT NOT NULL REFERENCES "Sessions" ("Id") ON DELETE CASCADE,
    Revision INTEGER NOT NULL CHECK (Revision > 0),
    Content TEXT NOT NULL CHECK (length(CAST(Content AS BLOB)) <= 8192),
    ContentDigest BLOB NOT NULL CHECK (length(ContentDigest) = 32),
    SensitivityCode INTEGER NOT NULL CHECK (SensitivityCode IN (0, 1)),
    SensitivityDigest BLOB NOT NULL CHECK (length(SensitivityDigest) = 32),
    SourceManifestDigest BLOB NOT NULL CHECK (length(SourceManifestDigest) = 32),
    SourceGeneration INTEGER NOT NULL CHECK (SourceGeneration >= 0),
    SummarizedThroughSequence INTEGER NOT NULL CHECK (SummarizedThroughSequence >= 0),
    CreatedAtUtc TEXT NOT NULL,
    ProducingMaintenanceReceiptDigest BLOB NULL CHECK (ProducingMaintenanceReceiptDigest IS NULL OR length(ProducingMaintenanceReceiptDigest) = 32)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_campaign_contribution_artifacts_revision ON campaign_contribution_artifacts(SessionId, Revision);
CREATE UNIQUE INDEX IF NOT EXISTS ux_campaign_contribution_artifacts_candidate ON campaign_contribution_artifacts(ArtifactId, SessionId, CampaignId, Revision);
CREATE INDEX IF NOT EXISTS idx_campaign_contribution_artifacts_campaign ON campaign_contribution_artifacts(CampaignId, SessionId);
