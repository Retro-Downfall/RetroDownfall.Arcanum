using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>Version 21, captured before outstanding reservations gained a Status index.</summary>
internal static class CoreSchemaVersionTwentyOneFixture
{
    internal const string PublishedFingerprint = "868DC0B0ADF204E1037B2970EAF8BAB780029297C7E17F1336214D7DCABD4E42";

    private const string BudgetReservationsSql =
        """
        -- The two exact USD columns are at the version-9 ADD COLUMN tail so fresh and evolved catalogs
        -- converge to the same normalized SQLite definition.
        CREATE TABLE IF NOT EXISTS "BudgetReservations" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_BudgetReservations" PRIMARY KEY,
            "RunId" TEXT NOT NULL,
            "BudgetPeriod" TEXT NOT NULL,
            "Status" INTEGER NOT NULL,
            "ExpiresAt" TEXT NOT NULL,
            "CreatedAt" TEXT NOT NULL,
            "UpdatedAt" TEXT NOT NULL
        , "ReservedUsd" TEXT NOT NULL DEFAULT '0' CHECK (typeof("ReservedUsd") = 'text'),
            "ReconciledUsd" TEXT NOT NULL DEFAULT '0' CHECK (typeof("ReconciledUsd") = 'text'));

        CREATE INDEX IF NOT EXISTS "IX_BudgetReservations_BudgetPeriod_Status" ON "BudgetReservations" ("BudgetPeriod", "Status");
        CREATE INDEX IF NOT EXISTS "IX_BudgetReservations_ExpiresAt" ON "BudgetReservations" ("ExpiresAt");
        """;

    internal static IReadOnlyList<GrimoireSchemaObject> Objects =>
    [
        .. CoreSchemaVersionTwentyTwoFixture.Objects.Select(item => item.Name == "BudgetReservations"
            ? item with { Sql = BudgetReservationsSql }
            : item),
    ];

    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeSourceFingerprint(Objects);

    internal static GrimoireSchemaVersionChainSet ChainSet() => new(
    [
        new GrimoireSchemaVersionChain(
            GrimoireSchemaManifestBuilder.Build(GrimoireSchemaFamily.Core, GrimoireSchemaTransactionTier.Core, 21, Fingerprint, Objects),
            Objects,
            [.. GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core).Steps.Where(step => step.ToVersion <= 21)]),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
    ]);
}
