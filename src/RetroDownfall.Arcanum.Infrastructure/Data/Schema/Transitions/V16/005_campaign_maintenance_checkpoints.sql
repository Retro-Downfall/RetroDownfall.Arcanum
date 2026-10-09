-- Companion steps leave the original four-step claim mask and durable evidence unchanged.
CREATE TABLE IF NOT EXISTS campaign_maintenance_checkpoints (
    ClaimId TEXT NOT NULL REFERENCES session_turn_claims(ClaimId) ON DELETE CASCADE,
    StepCode INTEGER NOT NULL CHECK (StepCode IN (5, 6)),
    CampaignId TEXT NOT NULL REFERENCES "Campaigns" ("Id") ON DELETE CASCADE,
    SessionId TEXT NOT NULL REFERENCES "Sessions" ("Id") ON DELETE CASCADE,
    SourceSessionId TEXT NULL REFERENCES "Sessions" ("Id") ON DELETE CASCADE,
    ExecutorId TEXT NOT NULL,
    ExpectedOutputRevision INTEGER NOT NULL CHECK (ExpectedOutputRevision >= 0),
    PhysicalProviderAttemptOrdinal INTEGER NOT NULL DEFAULT 0 CHECK (PhysicalProviderAttemptOrdinal >= 0),
    CheckpointStateCode INTEGER NOT NULL CHECK (CheckpointStateCode IN (1, 2, 3)),
    InputSourceGeneration INTEGER NOT NULL CHECK (InputSourceGeneration >= 0),
    InputManifestDigest BLOB NOT NULL CHECK (length(InputManifestDigest) = 32),
    ProviderCallDigest BLOB NULL CHECK (ProviderCallDigest IS NULL OR length(ProviderCallDigest) = 32),
    DisclosureReceiptDigest BLOB NULL CHECK (DisclosureReceiptDigest IS NULL OR length(DisclosureReceiptDigest) = 32),
    OutputArtifactKindCode INTEGER NULL CHECK (OutputArtifactKindCode IN (14, 15)),
    OutputArtifactId TEXT NULL,
    OutputRevision INTEGER NULL CHECK (OutputRevision > 0),
    OutputContentDigest BLOB NULL CHECK (OutputContentDigest IS NULL OR length(OutputContentDigest) = 32),
    OutputSensitivityDigest BLOB NULL CHECK (OutputSensitivityDigest IS NULL OR length(OutputSensitivityDigest) = 32),
    CheckpointRevision INTEGER NOT NULL CHECK (CheckpointRevision >= 0),
    UpdatedAtUtc TEXT NOT NULL,
    PRIMARY KEY (ClaimId, StepCode, InputManifestDigest),
    CHECK ((StepCode = 5 AND SourceSessionId IS NULL) OR (StepCode = 6 AND SourceSessionId IS NOT NULL)),
    CHECK ((CheckpointStateCode = 2 AND OutputArtifactId IS NOT NULL AND OutputRevision IS NOT NULL
            AND OutputContentDigest IS NOT NULL AND OutputSensitivityDigest IS NOT NULL
            AND ((StepCode = 5 AND OutputArtifactKindCode = 14) OR (StepCode = 6 AND OutputArtifactKindCode = 15)))
        OR (CheckpointStateCode IN (1, 3) AND OutputArtifactKindCode IS NULL AND OutputArtifactId IS NULL
            AND OutputRevision IS NULL AND OutputContentDigest IS NULL AND OutputSensitivityDigest IS NULL))
);
CREATE INDEX IF NOT EXISTS idx_campaign_maintenance_checkpoints_campaign ON campaign_maintenance_checkpoints(CampaignId);
