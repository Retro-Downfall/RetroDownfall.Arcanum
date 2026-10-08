using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>The exact normalized Core version-14 tree before the file-identity indexes were added.</summary>
/// <remarks>
/// Version 15 appends six indexes to <c>Batches</c> (three on the canonical file roles and three normalized ones
/// the retention sweep compares), one to <c>InferenceRuns</c> and one to <c>tapestry_generations</c>, and adds the
/// <c>tapestry_leaf_hashes</c> table with the nine triggers that keep it honest, and changes nothing else in the
/// tree. Those three files are frozen here byte for byte and the ten new objects (the table and its nine triggers)
/// are removed, because the version-13 reconstruction, and through it the version-12 one and the raw
/// version-1 to version-5 fixtures, inherit every object they do not freeze through this one.
/// </remarks>
internal static class CoreSchemaVersionFourteenFixture
{
    internal const string PublishedFingerprint =
        "F699757C9C5F2EDA486ECBF0CD1017762936D5730B8C01557FB33E5338347372";

    // Version 15 appends IX_Batches_InputFileId, IX_Batches_OutputFileId and IX_Batches_ErrorFileId, and their
    // normalized IX_Batches_InputFileId_Norm, IX_Batches_OutputFileId_Norm and IX_Batches_ErrorFileId_Norm.
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

    // Version 15 appends IX_InferenceRuns_SessionId_Norm.
    private const string InferenceRunsSql =
        """
        CREATE TABLE IF NOT EXISTS "InferenceRuns" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_InferenceRuns" PRIMARY KEY,
            "RequestId" TEXT NOT NULL,
            "SessionId" TEXT NULL,
            "Surface" TEXT NOT NULL,
            "Purpose" TEXT NOT NULL,
            "StartedAt" TEXT NOT NULL,
            "CompletedAt" TEXT NULL,
            "Status" INTEGER NOT NULL,
            "IdempotencyClaimId" TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS "IX_InferenceRuns_StartedAt" ON "InferenceRuns" ("StartedAt");
        CREATE INDEX IF NOT EXISTS "IX_InferenceRuns_IdempotencyClaimId" ON "InferenceRuns" ("IdempotencyClaimId");

        """;

    // Version 15 appends ux_tapestry_generations_complete_scope.
    private const string TapestryGenerationsSql =
        """
        CREATE TABLE IF NOT EXISTS tapestry_generations (
            GenerationId TEXT PRIMARY KEY,
            ScopeKind TEXT NOT NULL,
            ScopeId TEXT NOT NULL,
            Status TEXT NOT NULL,
            AlgorithmVersion TEXT NOT NULL,
            SettingsFingerprint TEXT NOT NULL,
            SummaryModel TEXT,
            SummaryRecipeVersion TEXT NOT NULL,
            EmbeddingDimension INTEGER NOT NULL,
            CorpusFingerprint TEXT NOT NULL,
            LayerCount INTEGER NOT NULL DEFAULT 0,
            NodeCount INTEGER NOT NULL DEFAULT 0,
            RootNodeCount INTEGER NOT NULL DEFAULT 0,
            TerminalReason TEXT,
            StartedAt TEXT NOT NULL,
            CompletedAt TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_tapestry_generations_scope
            ON tapestry_generations(ScopeKind, ScopeId, Status);

        """;

    // Version 15 adds the leaf-hash table and, per corpus table, one trigger each for insert, update and delete.
    private static bool IsVersionFifteenLeafHashObject(string name) =>
        name == "tapestry_leaf_hashes"
        || name.Contains("_drop_tapestry_leaf_hash_", StringComparison.Ordinal);

    internal static IReadOnlyList<GrimoireSchemaObject> Objects =>
    [
        .. GrimoireSchemaCatalog.CoreObjects
            .Where(static definition => !IsVersionFifteenLeafHashObject(definition.Name))
            .Select(static definition => definition.Name switch
            {
                "Batches" => definition with { Sql = BatchesSql.ReplaceLineEndings("\n") },

                "InferenceRuns" => definition with { Sql = InferenceRunsSql.ReplaceLineEndings("\n") },

                "tapestry_generations" => definition with { Sql = TapestryGenerationsSql.ReplaceLineEndings("\n") },

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
