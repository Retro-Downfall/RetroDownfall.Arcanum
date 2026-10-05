using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>The exact normalized Core version-13 tree before the session-key expression indexes were added.</summary>
/// <remarks>
/// Version 14 appends one expression index to each of <c>attachment_memory_consultations</c>,
/// <c>saga_extraction_watermarks</c> and <c>SessionContextPins</c> and changes nothing else. Each of those
/// files is frozen here byte for byte, because the version-12 reconstruction, and through it the raw
/// version-1 to version-5 fixtures, inherit every object they do not freeze through this one. It starts from
/// <see cref="CoreSchemaVersionFourteenFixture"/> rather than the head, so the objects version 15 changed
/// reach it, and every older fixture, as they were at version 14.
/// </remarks>
internal static class CoreSchemaVersionThirteenFixture
{
    internal const string PublishedFingerprint =
        "E46E5902803F25CD43236A77882E5B057374A7B902840E0AC1308427513A8D84";

    // Version 14 appends IX_attachment_memory_consultations_SessionId_Norm.
    private const string AttachmentMemoryConsultationsSql =
        """
        CREATE TABLE IF NOT EXISTS attachment_memory_consultations (
            SourceEntryId TEXT NOT NULL,
            SessionId TEXT NOT NULL,
            AttachmentId TEXT NOT NULL,
            LogicalKey TEXT NOT NULL,
            Version INTEGER NOT NULL,
            ContentHash TEXT NOT NULL,
            MaterializedAt TEXT NOT NULL,
            SourceType TEXT NOT NULL,
            PRIMARY KEY (SourceEntryId, AttachmentId, Version, MaterializedAt),
            FOREIGN KEY (SourceEntryId) REFERENCES Entries(Id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS IX_attachment_memory_consultations_Session_Time
        ON attachment_memory_consultations(SessionId, MaterializedAt);

        CREATE INDEX IF NOT EXISTS IX_attachment_memory_consultations_SourceEntry
        ON attachment_memory_consultations(SourceEntryId);

        """;

    // Version 14 appends IX_saga_extraction_watermarks_SessionId_Norm.
    private const string SagaExtractionWatermarksSql =
        """
        -- LastExtractedEntrySequence is laid out exactly as SQLite splices an added column into the stored
        -- table declaration. It begins NULL only on an inherited row while the version-7 backfill is
        -- pending; the table's insert and update guards refuse NULL and negative values on every later write.
        -- A fresh installation and one evolved through ALTER must normalize to the same definition, including
        -- the space SQLite places before the inserted comma.
        CREATE TABLE IF NOT EXISTS saga_extraction_watermarks (
            SessionId TEXT PRIMARY KEY,
            LastExtractedEntryCreatedAt TEXT NOT NULL
        , LastExtractedEntrySequence INTEGER);

        """;

    // Version 14 appends IX_SessionContextPins_SessionId_Norm.
    private const string SessionContextPinsSql =
        """
        CREATE TABLE IF NOT EXISTS "SessionContextPins" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_SessionContextPins" PRIMARY KEY,
            "SessionId" TEXT NOT NULL,
            "Kind" INTEGER NOT NULL,
            "TargetIdentifier" TEXT NOT NULL,
            "DisplayLabel" TEXT NOT NULL,
            "ContentVersion" TEXT NULL,
            "CreatedAt" TEXT NOT NULL,
            "UpdatedAt" TEXT NOT NULL,
            CONSTRAINT "FK_SessionContextPins_Sessions_SessionId"
                FOREIGN KEY ("SessionId") REFERENCES "Sessions" ("Id") ON DELETE CASCADE
        );

        CREATE UNIQUE INDEX IF NOT EXISTS "IX_SessionContextPins_SessionId_Kind_TargetIdentifier"
            ON "SessionContextPins" ("SessionId", "Kind", "TargetIdentifier");

        CREATE INDEX IF NOT EXISTS "IX_SessionContextPins_SessionId_UpdatedAt"
            ON "SessionContextPins" ("SessionId", "UpdatedAt");

        """;

    internal static IReadOnlyList<GrimoireSchemaObject> Objects =>
    [
        .. CoreSchemaVersionFourteenFixture.Objects
            .Select(static definition => definition.Name switch
            {
                "attachment_memory_consultations" => definition with { Sql = AttachmentMemoryConsultationsSql.ReplaceLineEndings("\n") },

                "saga_extraction_watermarks" => definition with { Sql = SagaExtractionWatermarksSql.ReplaceLineEndings("\n") },

                "SessionContextPins" => definition with { Sql = SessionContextPinsSql.ReplaceLineEndings("\n") },

                _ => definition,
            }),
    ];

    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeSourceFingerprint(Objects);

    /// <summary>The Core chain as this binary shipped it at version 13, with the other tiers unchanged.</summary>
    internal static GrimoireSchemaVersionChainSet ChainSet() =>
        new(
        [
            new GrimoireSchemaVersionChain(
                GrimoireSchemaManifestBuilder.Build(
                    GrimoireSchemaFamily.Core,
                    GrimoireSchemaTransactionTier.Core,
                    version: 13,
                    Fingerprint,
                    Objects),
                Objects,
                [
                    .. GrimoireSchemaVersionChains.Default
                        .ForTier(GrimoireSchemaTransactionTier.Core)
                        .Steps
                        .Where(static step => step.ToVersion <= 13),
                ]),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
        ]);
}
