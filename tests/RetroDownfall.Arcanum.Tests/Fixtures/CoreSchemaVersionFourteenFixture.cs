using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>The exact normalized Core version-14 tree before the file-identity indexes were added.</summary>
/// <remarks>
/// Version 15 appends three indexes to <c>Batches</c> and changes nothing else in the tree. That file is frozen
/// here byte for byte, because the version-13 reconstruction, and through it the version-12 one and the raw
/// version-1 to version-5 fixtures, inherit every object they do not freeze through this one.
/// </remarks>
internal static class CoreSchemaVersionFourteenFixture
{
    internal const string PublishedFingerprint =
        "F699757C9C5F2EDA486ECBF0CD1017762936D5730B8C01557FB33E5338347372";

    // Version 15 appends IX_Batches_InputFileId, IX_Batches_OutputFileId and IX_Batches_ErrorFileId.
    private const string BatchesSql =
        """
        CREATE TABLE IF NOT EXISTS "Batches" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_Batches" PRIMARY KEY,
            "InputFileId" TEXT NOT NULL,
            "Endpoint" TEXT NOT NULL,
            "Status" TEXT NOT NULL,
            "CreatedAt" TEXT NOT NULL,
            "CompletedAt" TEXT NULL,
            "OutputFileId" TEXT NULL,
            "ErrorFileId" TEXT NULL,
            "TotalRequestCount" INTEGER NOT NULL DEFAULT 0,
            "CompletedRequestCount" INTEGER NOT NULL DEFAULT 0,
            "FailedRequestCount" INTEGER NOT NULL DEFAULT 0,
            CONSTRAINT "CK_Batches_RequestCounts" CHECK (
                "TotalRequestCount" >= 0
                AND "CompletedRequestCount" >= 0
                AND "FailedRequestCount" >= 0
                AND "CompletedRequestCount" + "FailedRequestCount" <= "TotalRequestCount"
            )
        );

        CREATE INDEX IF NOT EXISTS "IX_Batches_Status" ON "Batches" ("Status");

        CREATE INDEX IF NOT EXISTS "IX_Batches_CreatedAt" ON "Batches" ("CreatedAt");

        CREATE INDEX IF NOT EXISTS "IX_Batches_CreatedAt_Id" ON "Batches" ("CreatedAt" DESC, "Id" DESC);

        """;

    internal static IReadOnlyList<GrimoireSchemaObject> Objects =>
    [
        .. GrimoireSchemaCatalog.CoreObjects
            .Select(static definition => definition.Name switch
            {
                "Batches" => definition with { Sql = BatchesSql.ReplaceLineEndings("\n") },

                _ => definition,
            }),
    ];

    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeSourceFingerprint(Objects);

    /// <summary>The Core chain as this binary shipped it at version 14, with the other tiers unchanged.</summary>
    internal static GrimoireSchemaVersionChainSet ChainSet() =>
        new(
        [
            new GrimoireSchemaVersionChain(
                GrimoireSchemaManifestBuilder.Build(
                    GrimoireSchemaFamily.Core,
                    GrimoireSchemaTransactionTier.Core,
                    version: 14,
                    Fingerprint,
                    Objects),
                Objects,
                [
                    .. GrimoireSchemaVersionChains.Default
                        .ForTier(GrimoireSchemaTransactionTier.Core)
                        .Steps
                        .Where(static step => step.ToVersion <= 14),
                ]),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
        ]);
}
