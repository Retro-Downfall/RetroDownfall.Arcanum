using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>
/// The Core head tree as it stood at schema version 7, reconstructed so version 8 can be exercised
/// from the schema an installed Grimoire actually published.
/// </summary>
/// <remarks>
/// Version 8 changed <c>Sessions</c>. Version 9 later changed the four accounting tables and added
/// the UTC inventory view. This fixture freezes all five earlier definitions and removes that view,
/// so versions 5 and 6 can continue peeling backward from a genuinely historical version-7 tree.
/// </remarks>
internal static class CoreSchemaVersionSevenFixture
{
    private const string BillableOperationsSql =
        """
        CREATE TABLE IF NOT EXISTS "BillableOperations" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_BillableOperations" PRIMARY KEY,
            "RunId" TEXT NOT NULL,
            "OperationType" INTEGER NOT NULL,
            "Provider" TEXT NOT NULL,
            "Model" TEXT NOT NULL,
            "Purpose" TEXT NOT NULL,
            "StartedAt" TEXT NOT NULL,
            "CompletedAt" TEXT NOT NULL,
            "InputTokens" INTEGER NOT NULL,
            "OutputTokens" INTEGER NOT NULL,
            "ReasoningTokens" INTEGER NOT NULL DEFAULT 0,
            "CachedTokens" INTEGER NOT NULL,
            "PricingSnapshotJson" TEXT NOT NULL,
            "ActualCostUsd" NUMERIC NOT NULL,
            "Status" INTEGER NOT NULL,
            "ProviderRequestId" TEXT NULL,
            CONSTRAINT "FK_BillableOperations_InferenceRuns_RunId" FOREIGN KEY ("RunId") REFERENCES "InferenceRuns" ("Id") ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS "IX_BillableOperations_CompletedAt" ON "BillableOperations" ("CompletedAt");
        CREATE INDEX IF NOT EXISTS "IX_BillableOperations_RunId" ON "BillableOperations" ("RunId");

        """;

    private const string BudgetAlertsSql =
        """
        CREATE TABLE IF NOT EXISTS "BudgetAlerts" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_BudgetAlerts" PRIMARY KEY,
            "Threshold" INTEGER NOT NULL,
            "AlertedAt" TEXT NOT NULL,
            "SpendUsd" NUMERIC NOT NULL,
            "DailyLimitUsd" NUMERIC NOT NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS "IX_BudgetAlerts_Threshold_Date" ON "BudgetAlerts" ("Threshold", date("AlertedAt"));

        """;

    private const string BudgetReservationsSql =
        """
        CREATE TABLE IF NOT EXISTS "BudgetReservations" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_BudgetReservations" PRIMARY KEY,
            "RunId" TEXT NOT NULL,
            "BudgetPeriod" TEXT NOT NULL,
            "ReservedUsd" NUMERIC NOT NULL,
            "ReconciledUsd" NUMERIC NOT NULL,
            "Status" INTEGER NOT NULL,
            "ExpiresAt" TEXT NOT NULL,
            "CreatedAt" TEXT NOT NULL,
            "UpdatedAt" TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS "IX_BudgetReservations_BudgetPeriod_Status" ON "BudgetReservations" ("BudgetPeriod", "Status");
        CREATE INDEX IF NOT EXISTS "IX_BudgetReservations_ExpiresAt" ON "BudgetReservations" ("ExpiresAt");

        """;

    private const string CostAdjustmentsSql =
        """
        CREATE TABLE IF NOT EXISTS "CostAdjustments" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_CostAdjustments" PRIMARY KEY,
            "BillableOperationId" TEXT NULL,
            "RunId" TEXT NULL,
            "AmountUsd" NUMERIC NOT NULL,
            "Reason" TEXT NOT NULL,
            "CreatedAt" TEXT NOT NULL
        );

        """;

    /// <summary>
    /// Sessions before version 8 declared <c>TotalCostUsd</c> with SQLite NUMERIC affinity rather than
    /// EF's exact decimal TEXT mapping.
    /// </summary>
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
            "TotalCostUsd" NUMERIC NOT NULL DEFAULT 0,
            "UnsummarizedEntryCount" INTEGER NOT NULL DEFAULT 0,
            "ForkedFromSessionId" TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS "IX_Sessions_CreatedAt" ON "Sessions" ("CreatedAt");

        CREATE INDEX IF NOT EXISTS "IX_Sessions_UpdatedAt" ON "Sessions" ("UpdatedAt");

        CREATE INDEX IF NOT EXISTS "IX_Sessions_Status" ON "Sessions" ("Status");

        CREATE INDEX IF NOT EXISTS "IX_Sessions_CampaignId" ON "Sessions" ("CampaignId");

        CREATE INDEX IF NOT EXISTS "IX_Sessions_Status_UpdatedAt" ON "Sessions" ("Status", "UpdatedAt");

        CREATE INDEX IF NOT EXISTS "IX_Sessions_CampaignId_Status_UpdatedAt" ON "Sessions" ("CampaignId", "Status", "UpdatedAt");

        CREATE INDEX IF NOT EXISTS "IX_Sessions_UnsummarizedEntryCount" ON "Sessions" ("UnsummarizedEntryCount");

        CREATE INDEX IF NOT EXISTS "IX_Sessions_ForkedFromSessionId" ON "Sessions" ("ForkedFromSessionId");

        """;

    internal static IReadOnlyList<GrimoireSchemaObject> Objects =>
    [
        .. GrimoireSchemaCatalog.CoreObjects
            .Where(static definition => definition.Name is not "grimoire_utc_instant_columns"
                and not "BatchAccountingRecoveryClaims")
            .Select(static definition => definition.Name switch
            {
                "BillableOperations" => WithSql(definition, BillableOperationsSql),

                "BudgetAlerts" => WithSql(definition, BudgetAlertsSql),

                "BudgetReservations" => WithSql(definition, BudgetReservationsSql),

                "CostAdjustments" => WithSql(definition, CostAdjustmentsSql),

                "Sessions" => WithSql(definition, SessionsSql),

                _ => definition,
            }),
    ];

    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeSourceFingerprint(Objects);

    internal static GrimoireSchemaVersionChainSet ChainSet() =>
        new(
        [
            new GrimoireSchemaVersionChain(
                GrimoireSchemaManifestBuilder.Build(
                    GrimoireSchemaFamily.Core,
                    GrimoireSchemaTransactionTier.Core,
                    version: 7,
                    Fingerprint,
                    Objects),
                Objects,
                [
                    .. GrimoireSchemaVersionChains.Default
                        .ForTier(GrimoireSchemaTransactionTier.Core)
                        .Steps
                        .Where(static step => step.ToVersion <= 7),
                ]),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
        ]);

    private static GrimoireSchemaObject WithSql(
        GrimoireSchemaObject definition,
        string sql) =>
        definition with
        {
            Sql = sql.ReplaceLineEndings("\n"),
        };
}
