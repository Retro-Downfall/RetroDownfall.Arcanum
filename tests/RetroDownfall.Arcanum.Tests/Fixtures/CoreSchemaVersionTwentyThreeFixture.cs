using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>Version 23, frozen before indexing Campaign continuity dependency lookups.</summary>
internal static class CoreSchemaVersionTwentyThreeFixture
{
    internal const string PublishedFingerprint = "2861C4C010C5398840BC2B626ED7FEED910E2882B0ABAD5CBC9DD7F15C669303";

    private static readonly IReadOnlyDictionary<string, string> FrozenTables = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["campaign_rollup_sources"] =
            """
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
            """,
        ["campaign_maintenance_checkpoints"] =
            """
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
            """,
    };

    internal static IReadOnlyList<GrimoireSchemaObject> Objects =>
    [
        .. CoreSchemaVersionTwentyFourFixture.Objects.Select(item => FrozenTables.TryGetValue(item.Name, out string? sql)
            ? item with { Sql = sql + "\n" }
            : item),
    ];

    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeSourceFingerprint(Objects);

    internal static GrimoireSchemaVersionChainSet ChainSet() => new(
    [
        new GrimoireSchemaVersionChain(
            GrimoireSchemaManifestBuilder.Build(GrimoireSchemaFamily.Core, GrimoireSchemaTransactionTier.Core, 23, Fingerprint, Objects),
            Objects,
            [.. GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core).Steps.Where(step => step.ToVersion <= 23)]),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
    ]);
}
