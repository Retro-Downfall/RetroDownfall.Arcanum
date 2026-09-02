using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>
/// The Core head tree as it stood at schema version 5, reconstructed so an upgrade can be driven from a
/// real version-5 installation rather than from a hand-written metadata row.
/// </summary>
/// <remarks>
/// Version 6 only <i>edits</i>: it declares no new head object, so removal reconstructs nothing and the
/// whole of the reconstruction is the frozen version-5 text of the four files it changes - three that
/// gain an expression index and one that gains a column. That is what keeps it honest: the fingerprint
/// this list produces is compared against the pin the shipped chain carries for version 6, so a
/// reconstruction that drifted fails rather than quietly certifying the wrong pin.
///
/// <para>Every further Core object whose shipped text moves after version 6 needs its version-5 text
/// frozen here beside the four that already are, or the reconstruction stops describing version 5 and
/// the pin assertion says so. A version-6 statement is the usual reason the text moves and it is not
/// the only one: the pinned fingerprint is taken over the file rather than over the SQL it holds, so a
/// corrected comment moves it too.</para>
///
/// <para>The fingerprint here is the <i>raw</i> one, and permanently so.
/// <see cref="GrimoireSchemaCatalog.ComputeSourceFingerprint"/> normalizes from Core version 6 onward,
/// but what a version-5 installation recorded was taken before that, so reproducing the pin means
/// reproducing the computation that made it.</para>
/// </remarks>
internal static class CoreSchemaVersionFiveFixture
{

    /// <summary><c>Entries</c> before version 6 appended the expression index the retention sweep reads.</summary>
    /// <remarks>
    /// Version 6 adds IX_Entries_SessionId_Norm, and the fingerprint reads the file, so the version-5 text
    /// has to be frozen here for the reconstruction to keep describing version 5.
    /// </remarks>
    private const string EntriesSql =
        """
        CREATE TABLE IF NOT EXISTS "Entries" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_Entries" PRIMARY KEY,
            "SessionId" TEXT NOT NULL,
            "Role" INTEGER NOT NULL,
            "Content" TEXT NOT NULL,
            "ModelUsed" TEXT NOT NULL,
            "CreatedAt" TEXT NOT NULL,
            "Sequence" INTEGER NOT NULL,
            "ToolCallId" TEXT NULL,
            "ToolName" TEXT NULL,
            "ToolArguments" TEXT NULL,
            "IsPinned" INTEGER NOT NULL DEFAULT 0,
            CONSTRAINT "FK_Entries_Sessions_SessionId" FOREIGN KEY ("SessionId") REFERENCES "Sessions" ("Id") ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS "IX_Entries_SessionId_CreatedAt" ON "Entries" ("SessionId", "CreatedAt");

        -- Authoritative intra-session chronological order. Unique so a lost per-session allocation races
        -- into a write failure instead of silently reordering a transcript.
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_Entries_SessionId_Sequence" ON "Entries" ("SessionId", "Sequence");

        CREATE INDEX IF NOT EXISTS "IX_Entries_Role" ON "Entries" ("Role");

        CREATE INDEX IF NOT EXISTS "IX_Entries_SessionId_IsPinned" ON "Entries" ("SessionId", "IsPinned");

        """;
    /// <summary><c>entry_embeddings</c> before version 6 appended its expression index.</summary>
    /// <remarks>
    /// The version-5 file declares the table and nothing else; version 6 gives it
    /// IX_entry_embeddings_EntryId_Norm.
    /// </remarks>
    private const string entryembeddingsSql =
        """
        CREATE TABLE IF NOT EXISTS entry_embeddings (
            EntryId TEXT PRIMARY KEY,
            Embedding BLOB NOT NULL,
            Dim INTEGER NOT NULL
        );

        """;
    /// <summary><c>SessionAttachments</c> before version 6 appended its two expression indexes.</summary>
    /// <remarks>
    /// Version 6 adds IX_SessionAttachments_SessionId_Norm and IX_SessionAttachments_Id_Norm below the
    /// indexes already declared here.
    /// </remarks>
    private const string SessionAttachmentsSql =
        """
        CREATE TABLE IF NOT EXISTS "SessionAttachments" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_SessionAttachments" PRIMARY KEY,
            "SessionId" TEXT NULL,
            "EntryId" TEXT NULL,
            "PendingTurnId" TEXT NULL,
            "State" TEXT NOT NULL,
            "LogicalKey" TEXT NOT NULL,
            "OriginalFileName" TEXT NOT NULL,
            "Version" INTEGER NOT NULL,
            "RelativePath" TEXT NOT NULL,
            "ContentSha256" TEXT NOT NULL,
            "MimeType" TEXT NOT NULL,
            "ByteLength" INTEGER NOT NULL,
            "Kind" TEXT NOT NULL,
            "CreatedAt" TEXT NOT NULL,
            "SourceKind" TEXT NOT NULL DEFAULT 'SnapshotOnly',
            "SourceWorkspaceIdentity" TEXT NULL,
            "SourceRelativePath" TEXT NULL,
            "SourceCanonicalPath" TEXT NULL,
            "SourceContentSha256" TEXT NULL,
            "SourceFileIdentity" TEXT NULL,
            "SourceLastWriteAt" TEXT NULL,
            "SourceByteLength" INTEGER NULL,
            "SourceStatus" TEXT NOT NULL DEFAULT 'NotApplicable',
            "SourceDiagnosticReason" TEXT NULL,
            "EncryptionVersion" INTEGER NOT NULL DEFAULT 0,
            "EncryptionKeyId" TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS "IX_SessionAttachments_Session_Logical_Version"
          ON "SessionAttachments" ("SessionId", "LogicalKey", "Version");

        CREATE INDEX IF NOT EXISTS "IX_SessionAttachments_Session_CreatedAt"
          ON "SessionAttachments" ("SessionId", "CreatedAt");

        CREATE INDEX IF NOT EXISTS "IX_SessionAttachments_EntryId"
          ON "SessionAttachments" ("EntryId");

        CREATE INDEX IF NOT EXISTS "IX_SessionAttachments_PendingTurnId"
          ON "SessionAttachments" ("PendingTurnId");

        CREATE INDEX IF NOT EXISTS "IX_SessionAttachments_State"
          ON "SessionAttachments" ("State");

        CREATE INDEX IF NOT EXISTS "IX_SessionAttachments_SourceWorkspace_Path"
          ON "SessionAttachments" ("SourceWorkspaceIdentity", "SourceRelativePath");

        CREATE UNIQUE INDEX IF NOT EXISTS UX_SessionAttachments_Bound
          ON SessionAttachments(SessionId, LogicalKey, Version)
          WHERE State = 'Bound';

        CREATE UNIQUE INDEX IF NOT EXISTS UX_SessionAttachments_Pending
          ON SessionAttachments(PendingTurnId, LogicalKey, Version)
          WHERE State = 'Pending';

        """;
    /// <summary><c>workspace_file_chunks</c> before version 6 gave it a FileLength column.</summary>
    /// <remarks>
    /// The one object version 6 alters rather than only extends. Its version-6 text carries the column
    /// spliced in the shape ALTER TABLE ... ADD COLUMN produces, so the version-5 text is the
    /// declaration without it.
    /// </remarks>
    private const string workspacefilechunksSql =
        """
        CREATE TABLE IF NOT EXISTS workspace_file_chunks (
            ChunkId TEXT PRIMARY KEY,
            WorkspacePath TEXT NOT NULL,
            RelativePath TEXT NOT NULL,
            ChunkIndex INTEGER NOT NULL,
            Content TEXT NOT NULL,
            CharOffset INTEGER NOT NULL,
            CharLength INTEGER NOT NULL,
            StartLine INTEGER NOT NULL DEFAULT 1,
            EndLine INTEGER NOT NULL DEFAULT 1,
            FileLastWriteTime TEXT NOT NULL,
            IndexedAt TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_workspace_file_chunks_path
        ON workspace_file_chunks(WorkspacePath, RelativePath);

        """;
    /// <summary>Every Core object as version 5 declared it.</summary>
    /// <remarks>
    /// Line endings are normalized because the frozen text above is a C# literal and the catalog's text
    /// is an embedded file. A checkout that handed one of them CRLF would move the fingerprint without
    /// changing a single character of SQL.
    /// </remarks>
    internal static IReadOnlyList<GrimoireSchemaObject> Objects =>
    [
        .. GrimoireSchemaCatalog.CoreObjects
            .Select(static definition => definition.Name switch
            {

                "Entries" => definition with
                {
                    Sql = EntriesSql.ReplaceLineEndings("\n"),
                },

                "entry_embeddings" => definition with
                {
                    Sql = entryembeddingsSql.ReplaceLineEndings("\n"),
                },

                "SessionAttachments" => definition with
                {
                    Sql = SessionAttachmentsSql.ReplaceLineEndings("\n"),
                },

                "workspace_file_chunks" => definition with
                {
                    Sql = workspacefilechunksSql.ReplaceLineEndings("\n"),
                },

                _ => definition,

            }),
    ];

    /// <summary>The fingerprint the version-5 tree published, computed from the reconstruction above.</summary>
    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeRawSourceFingerprint(Objects);

    /// <summary>
    /// An installable version-5 chain set: the reconstructed Core tree at version 5 with the four steps
    /// that reach it, and the shipped chains for both Covenant tiers.
    /// </summary>
    /// <remarks>
    /// Installing this and then handing the same installer <see cref="GrimoireSchemaVersionChains.Default"/>
    /// is the whole of an upgrade as a caller reaches it. Nothing writes a metadata row or a journal row
    /// to describe the older installation, so no assertion rests on a state a test invented.
    /// </remarks>
    internal static GrimoireSchemaVersionChainSet ChainSet() =>
        new(
        [
            new GrimoireSchemaVersionChain(
                GrimoireSchemaManifestBuilder.Build(
                    GrimoireSchemaFamily.Core,
                    GrimoireSchemaTransactionTier.Core,
                    version: 5,
                    Fingerprint,
                    Objects),
                Objects,
                [
                    .. GrimoireSchemaVersionChains.Default
                        .ForTier(GrimoireSchemaTransactionTier.Core)
                        .Steps
                        .Where(static step => step.ToVersion <= 5),
                ]),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
        ]);

}
