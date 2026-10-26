using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>Approved Core26 DAG schema, frozen before source revision triggers were added.</summary>
internal static class CoreSchemaVersionTwentySixFixture
{
    internal const string PublishedFingerprint = "9B9BE8D38636742B481D8C4A35BC1C84262AC4DA99373652B22FBB44517462A9";

    private static readonly HashSet<string> LaterObjects = new(StringComparer.Ordinal)
    {
        "workspace_file_chunks_tapestry_revision_insert",
        "workspace_file_chunks_tapestry_revision_replace_update",
        "workspace_file_chunks_tapestry_revision_update",
        "workspace_file_chunks_tapestry_revision_delete",
        "Entries_tapestry_revision_insert",
        "Entries_tapestry_revision_replace_update",
        "Entries_tapestry_revision_update",
        "Entries_tapestry_revision_delete",
        "session_attachment_chunks_tapestry_revision_insert",
        "session_attachment_chunks_tapestry_revision_replace_update",
        "session_attachment_chunks_tapestry_revision_update",
        "session_attachment_chunks_tapestry_revision_delete",
    };

    private static readonly IReadOnlyDictionary<string, string> Frozen = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["workspace_file_chunks"] =
            """
            -- FileLength is laid out the way SQLite lays out an added column, not the way the rest of this file is
            -- indented, and that is deliberate. Version 6 reaches an existing installation through
            -- ALTER TABLE ... ADD COLUMN, which rewrites the stored table declaration by splicing ", <column-def>"
            -- in front of the closing parenthesis and taking the definition verbatim. The installer then compares
            -- that stored text with this file, normalized. An installation built fresh from this file and one
            -- evolved through the ALTER have to normalize to the same string, so this file has to be written in the
            -- shape ALTER produces. Reindenting it reports DefinitionDrift on every evolved installation and on none
            -- of the fresh ones, which is the hardest shape of that failure to reproduce.
            --
            -- It is here because FileLastWriteTime alone cannot answer "has this file changed". The sweep compares
            -- the recorded timestamp for equality, so a rewrite that lands on the recorded value - a coarse-grained
            -- filesystem stamping within the same tick, an archive extraction restoring what it captured, a tool
            -- that puts the timestamp back on purpose - makes the file invisible to every later tick, permanently,
            -- because nothing else revisits it. The length is the second signal, and it is a signal rather than a
            -- proof: a rewrite preserving both the timestamp and the byte count is still missed. Hashing every
            -- candidate on every tick would be a proof, and would read the whole workspace to find the nothing that
            -- usually changed.
            --
            -- -1 means "no length was recorded", which is what every row an upgrade inherits holds. It is not a
            -- length any file can have, so the comparison treats such a row as changed and re-indexes it once,
            -- after which it holds a real value. The same sentinel is written at insert and replaced by the
            -- metadata pass that stamps the timestamp, so a file whose indexing was interrupted is re-indexed for
            -- the same reason rather than being trusted half-written.
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
            , FileLength INTEGER NOT NULL DEFAULT -1);

            CREATE INDEX IF NOT EXISTS idx_workspace_file_chunks_path
            ON workspace_file_chunks(WorkspacePath, RelativePath);
            """,
        ["Entries"] =
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

            -- Retention compares this column normalized - lower(replace(SessionId, '-', '')) - because a stored
            -- identity once had two spellings and an exact equality would have missed one of them. SQLite cannot
            -- answer a function-wrapped column from IX_Entries_SessionId_CreatedAt or from any other index above,
            -- so every one of those comparisons was a full scan of this table, once per candidate Session in the
            -- planning pass and again per candidate in the apply pass. The expression here has to stay character
            -- for character the shape the predicate has, because that is how SQLite decides the index applies.
            CREATE INDEX IF NOT EXISTS "IX_Entries_SessionId_Norm"
              ON "Entries" (lower(replace("SessionId", '-', '')));

            CREATE INDEX IF NOT EXISTS "IX_Entries_Id_Norm" ON "Entries" (lower(replace("Id", '-', '')));
            """,
        ["session_attachment_chunks"] =
            """
            CREATE TABLE IF NOT EXISTS session_attachment_chunks (
                ChunkId TEXT PRIMARY KEY,
                GenerationId TEXT NOT NULL DEFAULT 'legacy',
                SessionId TEXT NOT NULL,
                AttachmentId TEXT NOT NULL,
                LogicalKey TEXT NOT NULL,
                Version INTEGER NOT NULL,
                OriginalFileName TEXT NOT NULL,
                MimeType TEXT NOT NULL,
                ContentSha256 TEXT NOT NULL,
                ChunkIndex INTEGER NOT NULL,
                CharacterStart INTEGER NOT NULL,
                CharacterEnd INTEGER NOT NULL,
                StartLine INTEGER NOT NULL,
                EndLine INTEGER NOT NULL,
                Content TEXT NOT NULL,
                EmbeddingDimension INTEGER NOT NULL,
                ExtractedAt TEXT NOT NULL,
                IndexedAt TEXT NOT NULL,
                RetrievalScope TEXT,
                UNIQUE(AttachmentId, GenerationId, ChunkIndex),
                FOREIGN KEY(AttachmentId) REFERENCES SessionAttachments(Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_session_attachment_chunks_session
                ON session_attachment_chunks(SessionId, RetrievalScope);
            CREATE INDEX IF NOT EXISTS idx_session_attachment_chunks_attachment
                ON session_attachment_chunks(AttachmentId);
            """,
    };

    internal static IReadOnlyList<GrimoireSchemaObject> Objects =>
    [
        .. GrimoireSchemaCatalog.CoreObjects.Where(item => !LaterObjects.Contains(item.Name))
            .Select(item => Frozen.TryGetValue(item.Name, out string? sql) ? item with { Sql = sql + "\n" } : item),
    ];

    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeSourceFingerprint(Objects);

    internal static GrimoireSchemaVersionChainSet ChainSet() => new(
    [
        new GrimoireSchemaVersionChain(
            GrimoireSchemaManifestBuilder.Build(GrimoireSchemaFamily.Core, GrimoireSchemaTransactionTier.Core, 26, Fingerprint, Objects),
            Objects,
            [.. GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core).Steps.Where(step => step.ToVersion <= 26)]),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
    ]);
}
