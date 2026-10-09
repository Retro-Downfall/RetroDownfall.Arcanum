-- A null pointer with refold debt cannot expose an aggregate made from obsolete contributors.
CREATE TABLE IF NOT EXISTS campaign_rollup_state (
    CampaignId TEXT NOT NULL PRIMARY KEY REFERENCES "Campaigns" ("Id") ON DELETE CASCADE,
    CurrentArtifactId TEXT NULL,
    Revision INTEGER NOT NULL DEFAULT 0 CHECK (Revision >= 0),
    SourceGeneration INTEGER NOT NULL DEFAULT 0 CHECK (SourceGeneration >= 0),
    RefoldRequired INTEGER NOT NULL DEFAULT 0 CHECK (RefoldRequired IN (0, 1)),
    LastFoldedSessionId TEXT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    FOREIGN KEY (CurrentArtifactId, CampaignId, Revision) REFERENCES campaign_rollup_artifacts(ArtifactId, CampaignId, Revision),
    CHECK (CurrentArtifactId IS NULL OR (Revision > 0 AND RefoldRequired = 0))
);
