CREATE TABLE IF NOT EXISTS campaign_contribution_state (
    SessionId TEXT NOT NULL PRIMARY KEY REFERENCES "Sessions" ("Id") ON DELETE CASCADE,
    CampaignId TEXT NOT NULL REFERENCES "Campaigns" ("Id") ON DELETE CASCADE,
    CurrentArtifactId TEXT NULL,
    Revision INTEGER NOT NULL DEFAULT 0 CHECK (Revision >= 0),
    SourceGeneration INTEGER NOT NULL DEFAULT 0 CHECK (SourceGeneration >= 0),
    SummarizedThroughSequence INTEGER NOT NULL DEFAULT 0 CHECK (SummarizedThroughSequence >= 0),
    RefoldRequired INTEGER NOT NULL DEFAULT 0 CHECK (RefoldRequired IN (0, 1)),
    UpdatedAtUtc TEXT NOT NULL,
    FOREIGN KEY (CurrentArtifactId, SessionId, CampaignId, Revision) REFERENCES campaign_contribution_artifacts(ArtifactId, SessionId, CampaignId, Revision),
    CHECK (CurrentArtifactId IS NULL OR (Revision > 0 AND RefoldRequired = 0))
);
CREATE INDEX IF NOT EXISTS idx_campaign_contribution_state_campaign ON campaign_contribution_state(CampaignId, SessionId);
