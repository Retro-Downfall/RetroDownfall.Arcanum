-- Immutable provenance segments outlive individual summary revisions, never their last root.
CREATE TABLE IF NOT EXISTS campaign_rollup_manifests (
    ManifestId TEXT NOT NULL PRIMARY KEY,
    LineageId TEXT NOT NULL,
    Ordinal INTEGER NOT NULL CHECK (Ordinal > 0),
    CampaignId TEXT NOT NULL REFERENCES Campaigns(Id) ON DELETE CASCADE,
    HashFormatCode INTEGER NOT NULL CHECK (HashFormatCode IN (1, 2)),
    PriorManifestDigest BLOB NOT NULL CHECK (length(PriorManifestDigest) = 32),
    SourceManifestDigest BLOB NOT NULL CHECK (length(SourceManifestDigest) = 32),
    SourceCount INTEGER NOT NULL CHECK (SourceCount > 0),
    AddedSourceCount INTEGER NOT NULL CHECK (AddedSourceCount > 0),
    UNIQUE (LineageId, Ordinal),
    UNIQUE (ManifestId, LineageId, CampaignId)
);
CREATE INDEX IF NOT EXISTS idx_campaign_rollup_manifests_campaign ON campaign_rollup_manifests(CampaignId);
