using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>
/// The normalized Core head tree captured immediately before version 9 changed authoritative USD
/// affinities and added the UTC instant inventory view.
/// </summary>
/// <remarks>
/// Version 8 changed one version-7 object: <c>Sessions.TotalCostUsd</c> moved to exact TEXT. Peeling
/// that single edit forward from <see cref="CoreSchemaVersionSevenFixture"/> prevents any version-9
/// object from leaking backward into this historical tree.
/// </remarks>
internal static class CoreSchemaVersionEightFixture
{
    private const string SessionsSql =
        """
        CREATE TABLE IF NOT EXISTS "Sessions" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_Sessions" PRIMARY KEY,
            "CampaignId" TEXT NULL,
            "Title" TEXT NULL,
            "Status" TEXT NOT NULL DEFAULT 'active',
            "CreatedAt" TEXT NOT NULL,
            "UpdatedAt" TEXT NOT NULL,
            "Summary" TEXT NULL,
            "LastSummarizedMessageAt" TEXT NULL,
            "TotalTokensUsed" INTEGER NOT NULL DEFAULT 0,
            "UnsummarizedEntryCount" INTEGER NOT NULL DEFAULT 0,
            "ForkedFromSessionId" TEXT NULL
        , "TotalCostUsd" TEXT NOT NULL DEFAULT 0);

        CREATE INDEX IF NOT EXISTS "IX_Sessions_CreatedAt" ON "Sessions" ("CreatedAt");

        CREATE INDEX IF NOT EXISTS "IX_Sessions_UpdatedAt" ON "Sessions" ("UpdatedAt");

        CREATE INDEX IF NOT EXISTS "IX_Sessions_Status" ON "Sessions" ("Status");

        CREATE INDEX IF NOT EXISTS "IX_Sessions_CampaignId" ON "Sessions" ("CampaignId");

        CREATE INDEX IF NOT EXISTS "IX_Sessions_Status_UpdatedAt" ON "Sessions" ("Status", "UpdatedAt");

        CREATE INDEX IF NOT EXISTS "IX_Sessions_CampaignId_Status_UpdatedAt" ON "Sessions" ("CampaignId", "Status", "UpdatedAt");

        CREATE INDEX IF NOT EXISTS "IX_Sessions_UnsummarizedEntryCount" ON "Sessions" ("UnsummarizedEntryCount");

        CREATE INDEX IF NOT EXISTS "IX_Sessions_ForkedFromSessionId" ON "Sessions" ("ForkedFromSessionId");

        """;

    internal const string PublishedFingerprint =
        "D1BC1D6158669F4E1070B7D7B00CA2F5697E3E6CCC9081EEA15167CE8D49C7B6";

    internal static IReadOnlyList<GrimoireSchemaObject> Objects =>
    [
        .. CoreSchemaVersionSevenFixture.Objects
            .Select(static definition => definition.Name == "Sessions"
                ? definition with { Sql = SessionsSql.ReplaceLineEndings("\n") }
                : definition),
    ];

    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeSourceFingerprint(Objects);

    internal static GrimoireSchemaVersionChainSet ChainSet() =>
        new(
        [
            new GrimoireSchemaVersionChain(
                GrimoireSchemaManifestBuilder.Build(
                    GrimoireSchemaFamily.Core,
                    GrimoireSchemaTransactionTier.Core,
                    version: 8,
                    Fingerprint,
                    Objects),
                Objects,
                [
                    .. GrimoireSchemaVersionChains.Default
                        .ForTier(GrimoireSchemaTransactionTier.Core)
                        .Steps
                        .Where(static step => step.ToVersion <= 8),
                ]),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
        ]);
}
