using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>Core24 exactly as published, before shared Campaign provenance.</summary>
internal static class CoreSchemaVersionTwentyFourFixture
{
    internal const string PublishedFingerprint = "2D7C2C480B7345D3CDBE7A6EAF05BB131E70D2ADB7E1BD940B45A32959609028";

    private static readonly HashSet<string> LaterObjects = new(StringComparer.Ordinal)
    {
        "campaign_rollup_sources_manifest_guard",
        "campaign_rollup_artifacts_manifest_delete",
        "campaign_rollup_artifacts_manifest_insert",
        "campaign_rollup_manifest_sources",
        "campaign_rollup_manifest_sources_campaign_delete",
        "campaign_rollup_manifest_sources_guard_delete",
        "campaign_rollup_manifest_sources_guard_insert",
        "campaign_rollup_manifest_sources_guard_update",
        "campaign_rollup_manifests",
        "campaign_rollup_manifests_guard_delete",
        "campaign_rollup_manifests_guard_update",
        "campaign_rollup_source_membership",
    };

    private static readonly IReadOnlyDictionary<string, string> Frozen = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["campaign_rollup_artifacts"] =
            """
            -- Immutable bounded publication revisions retain the bytes an unfinished turn admitted.
            CREATE TABLE IF NOT EXISTS campaign_rollup_artifacts (
                ArtifactId TEXT NOT NULL PRIMARY KEY,
                CampaignId TEXT NOT NULL REFERENCES "Campaigns" ("Id") ON DELETE CASCADE,
                Revision INTEGER NOT NULL CHECK (Revision > 0),
                Content TEXT NOT NULL CHECK (length(CAST(Content AS BLOB)) <= 8192),
                ContentDigest BLOB NOT NULL CHECK (length(ContentDigest) = 32),
                SensitivityCode INTEGER NOT NULL CHECK (SensitivityCode IN (0, 1)),
                SensitivityDigest BLOB NOT NULL CHECK (length(SensitivityDigest) = 32),
                SourceManifestDigest BLOB NOT NULL CHECK (length(SourceManifestDigest) = 32),
                SourceGeneration INTEGER NOT NULL CHECK (SourceGeneration >= 0),
                SourceCount INTEGER NOT NULL CHECK (SourceCount >= 0),
                CreatedAtUtc TEXT NOT NULL,
                ProducingMaintenanceReceiptDigest BLOB NULL CHECK (ProducingMaintenanceReceiptDigest IS NULL OR length(ProducingMaintenanceReceiptDigest) = 32)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_campaign_rollup_artifacts_revision ON campaign_rollup_artifacts(CampaignId, Revision);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_campaign_rollup_artifacts_candidate ON campaign_rollup_artifacts(ArtifactId, CampaignId, Revision);
            """,
        ["campaign_contribution_state_campaign_update"] =
            """
            -- New, unconsumed Sessions add incremental debt; changing a consumed source requires a refold.
            CREATE TRIGGER IF NOT EXISTS campaign_contribution_state_campaign_update
            AFTER UPDATE ON campaign_contribution_state
            WHEN NEW.CurrentArtifactId IS NOT OLD.CurrentArtifactId
            BEGIN
                UPDATE campaign_rollup_state
                SET CurrentArtifactId = CASE WHEN EXISTS (SELECT 1 FROM campaign_rollup_sources source
                        WHERE source.RollupArtifactId = campaign_rollup_state.CurrentArtifactId AND source.SessionId = OLD.SessionId) THEN NULL ELSE CurrentArtifactId END,
                    RefoldRequired = CASE WHEN EXISTS (SELECT 1 FROM campaign_rollup_sources source
                        WHERE source.RollupArtifactId = campaign_rollup_state.CurrentArtifactId AND source.SessionId = OLD.SessionId) THEN 1 ELSE RefoldRequired END,
                    SourceGeneration = SourceGeneration + 1,
                    LastFoldedSessionId = CASE WHEN EXISTS (SELECT 1 FROM campaign_rollup_sources source
                        WHERE source.RollupArtifactId = campaign_rollup_state.CurrentArtifactId AND source.SessionId = OLD.SessionId) THEN NULL ELSE LastFoldedSessionId END
                WHERE CampaignId = OLD.CampaignId;
            END;
            """,
        ["campaign_contribution_state_campaign_delete"] =
            """
            -- New, unconsumed Sessions add incremental debt; changing a consumed source requires a refold.
            CREATE TRIGGER IF NOT EXISTS campaign_contribution_state_campaign_delete
            AFTER DELETE ON campaign_contribution_state
            BEGIN
                UPDATE campaign_rollup_state
                SET CurrentArtifactId = CASE WHEN EXISTS (SELECT 1 FROM campaign_rollup_sources source
                        WHERE source.RollupArtifactId = campaign_rollup_state.CurrentArtifactId AND source.SessionId = OLD.SessionId) THEN NULL ELSE CurrentArtifactId END,
                    RefoldRequired = CASE WHEN EXISTS (SELECT 1 FROM campaign_rollup_sources source
                        WHERE source.RollupArtifactId = campaign_rollup_state.CurrentArtifactId AND source.SessionId = OLD.SessionId) THEN 1 ELSE RefoldRequired END,
                    SourceGeneration = SourceGeneration + 1,
                    LastFoldedSessionId = CASE WHEN EXISTS (SELECT 1 FROM campaign_rollup_sources source
                        WHERE source.RollupArtifactId = campaign_rollup_state.CurrentArtifactId AND source.SessionId = OLD.SessionId) THEN NULL ELSE LastFoldedSessionId END
                WHERE CampaignId = OLD.CampaignId;
            END;
            """,
    };

    internal static IReadOnlyList<GrimoireSchemaObject> Objects =>
    [
        .. CoreSchemaVersionTwentyFiveFixture.Objects.Where(item => !LaterObjects.Contains(item.Name))
            .Select(item => Frozen.TryGetValue(item.Name, out string? sql) ? item with { Sql = sql + "\n" } : item),
    ];

    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeSourceFingerprint(Objects);

    internal static GrimoireSchemaVersionChainSet ChainSet() => new(
    [
        new GrimoireSchemaVersionChain(
            GrimoireSchemaManifestBuilder.Build(GrimoireSchemaFamily.Core, GrimoireSchemaTransactionTier.Core, 24, Fingerprint, Objects),
            Objects,
            [.. GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core).Steps.Where(step => step.ToVersion <= 24)]),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
    ]);
}
