using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

/// <summary>
/// RAG Phase 1 — creates The Weave's embedding schema idempotently at Grimoire bootstrap, immediately
/// after <c>GrimoireSqlSchemaMigrator.ApplyPendingAsync</c> (see
/// <c>GrimoireDatabaseBootstrapper.EnsureInitializedAsync</c>). This is intentionally <b>not</b> a
/// registered migration in <c>GrimoireSqlSchemaMigrator.MigrationOrder</c>:
///
/// <list type="bullet">
/// <item>The BLOB metadata tables are raw-SQL-owned and reconciled idempotently here. Additive
/// workspace chunk metadata columns are detected with <c>PRAGMA table_info</c> and added in place,
/// preserving existing embeddings without entering the EF compiled model.</item>
/// <item>The vec0 table's vector column width must be interpolated from
/// <c>Arcanum:Integrations:Embeddings:Dimensions</c> at runtime; a static embedded <c>.sql</c> file
/// cannot express that.</item>
/// </list>
///
/// Two tables exist per RAG feature; Phase 1 wires up the first pair, for entries (used by Phase 2's
/// session semantic search):
///
/// <list type="bullet">
/// <item><c>entry_embeddings</c> (always created): the BLOB durable store and managed-fallback search
/// source of truth. <c>EntryId TEXT PRIMARY KEY</c> already gives an implicit unique index — no extra
/// index is needed in Phase 1.</item>
/// <item><c>entry_embeddings_vec</c> (created only when <see cref="SqliteVecExtensionLoader"/>
/// succeeds): the vec0 acceleration index, declared with an explicit <c>distance_metric=cosine</c> so
/// <c>similarity = 1.0 - distance</c> always holds — no version-specific distance-formula guessing. Its
/// columns are named <c>EntryId</c>/<c>Embedding</c> — matching <c>entry_embeddings</c> exactly (PascalCase,
/// this codebase's SQL identifier convention throughout, rather than sqlite-vec's own docs' snake_case
/// examples) — so <c>DivinationService</c> can pass one pair of column names to
/// <c>IDivinationService.SearchAsync</c> for both the vec0 and managed-fallback paths.</item>
/// </list>
///
/// Safety: every step here is wrapped so a sqlite-vec problem degrades to a logged warning, never a
/// startup failure — schema creation for the BLOB table alone is enough for Phase 2's managed-fallback
/// Divination to work.
/// </summary>
internal static class WeaveSchemaInitializer
{

    internal const int SchemaVersion = 2;

    public static async Task EnsureSchemaAsync(
        SqliteConnection connection,
        int configuredDimensions,
        WeaveIndexAvailability availability,
        ILogger? logger,
        CancellationToken cancellationToken)
    {

        try
        {

            await CreateEntryEmbeddingsTableAsync(connection, cancellationToken).ConfigureAwait(false);

            await CreateWorkspaceFileTablesAsync(connection, cancellationToken).ConfigureAwait(false);

            await CreateSagaMemoryTablesAsync(connection, cancellationToken).ConfigureAwait(false);

            bool vecAvailable = SqliteVecExtensionLoader.TryLoad(connection, logger);

            availability.SetAvailable(vecAvailable);

            if (vecAvailable)
            {

                await CreateEntryEmbeddingsVecTableAsync(connection, configuredDimensions, cancellationToken)
                    .ConfigureAwait(false);

                await CreateWorkspaceFileEmbeddingsVecTableAsync(connection, configuredDimensions, cancellationToken)
                    .ConfigureAwait(false);

                await CreateSagaMemoryEmbeddingsVecTableAsync(connection, configuredDimensions, cancellationToken)
                    .ConfigureAwait(false);

            }

            await WarnOnDimensionMismatchAsync(connection, configuredDimensions, logger, cancellationToken)
                .ConfigureAwait(false);

        }
        catch (Exception ex)
        {

            // The Weave is optional infrastructure (see class remarks): any failure here — sqlite-vec
            // quirks, a locked database during a rare race, etc. — must never fail Grimoire startup.
            availability.SetAvailable(false);

            logger?.LogWarning(
                ex,
                "The Weave schema initialization failed; RAG features relying on it will report unavailable until this is resolved.");

        }

    }

    private static async Task CreateEntryEmbeddingsTableAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {

        await using SqliteCommand cmd = connection.CreateCommand();

        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS entry_embeddings (
                EntryId TEXT PRIMARY KEY,
                Embedding BLOB NOT NULL,
                Dim INTEGER NOT NULL
            );
            """;

        _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static async Task CreateEntryEmbeddingsVecTableAsync(
        SqliteConnection connection,
        int dimensions,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand cmd = connection.CreateCommand();

        // dimensions is clamped configuration (see ArcanumSettingClamps.EmbeddingsDimensions, 64-4096),
        // never user input, so interpolating it into DDL uses the same trust model
        // GrimoireSqlSchemaMigrator already applies to its own (fixed) migration scripts.
        // Column names match entry_embeddings' PascalCase columns exactly (see class remarks).
        cmd.CommandText =
            $"""
            CREATE VIRTUAL TABLE IF NOT EXISTS entry_embeddings_vec USING vec0(
                EntryId TEXT PRIMARY KEY,
                Embedding FLOAT[{dimensions}] distance_metric=cosine
            );
            """;

        _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// RAG Phase 3 — the two tables backing semantic codebase retrieval. Always created (independent of
    /// sqlite-vec availability), mirroring <see cref="CreateEntryEmbeddingsTableAsync"/>:
    /// <list type="bullet">
    /// <item><c>workspace_file_embeddings</c>: the BLOB durable store and managed-fallback search source
    /// of truth, keyed by <c>ChunkId</c> (a synthetic identifier minted by <c>WorkspaceIndexingService</c>,
    /// not tied to any EF-managed Guid column — no case-normalization concerns here).</item>
    /// <item><c>workspace_file_chunks</c>: chunk metadata (workspace/file/offset/content) joined against
    /// Divination hits to render results. <c>idx_workspace_file_chunks_path</c> accelerates the
    /// change-detection and delete-before-reindex queries keyed by (WorkspacePath, RelativePath).</item>
    /// </list>
    /// </summary>
    private static async Task CreateWorkspaceFileTablesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {

        await using SqliteCommand blobCmd = connection.CreateCommand();

        blobCmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS workspace_file_embeddings (
                ChunkId TEXT PRIMARY KEY,
                Embedding BLOB NOT NULL,
                Dim INTEGER NOT NULL
            );
            """;

        _ = await blobCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteCommand chunksCmd = connection.CreateCommand();

        chunksCmd.CommandText =
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
            """;

        _ = await chunksCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await EnsureWorkspaceChunkColumnAsync(
            connection,
            "StartLine",
            cancellationToken).ConfigureAwait(false);

        await EnsureWorkspaceChunkColumnAsync(
            connection,
            "EndLine",
            cancellationToken).ConfigureAwait(false);

        await using SqliteCommand indexCmd = connection.CreateCommand();

        indexCmd.CommandText =
            """
            CREATE INDEX IF NOT EXISTS idx_workspace_file_chunks_path
            ON workspace_file_chunks(WorkspacePath, RelativePath);
            """;

        _ = await indexCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static async Task EnsureWorkspaceChunkColumnAsync(
        SqliteConnection connection,
        string columnName,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand inspect = connection.CreateCommand();

        inspect.CommandText = """PRAGMA table_info("workspace_file_chunks");""";

        await using SqliteDataReader reader = await inspect.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            if (string.Equals(reader.GetString(1), columnName, StringComparison.Ordinal))
            {

                return;

            }

        }

        await reader.DisposeAsync().ConfigureAwait(false);

        await using SqliteCommand alter = connection.CreateCommand();

        alter.CommandText = columnName switch
        {
            "StartLine" => """ALTER TABLE "workspace_file_chunks" ADD COLUMN "StartLine" INTEGER NOT NULL DEFAULT 1;""",
            "EndLine" => """ALTER TABLE "workspace_file_chunks" ADD COLUMN "EndLine" INTEGER NOT NULL DEFAULT 1;""",
            _ => throw new ArgumentOutOfRangeException(nameof(columnName)),
        };

        _ = await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// RAG Phase 3 — vec0 acceleration for <c>workspace_file_embeddings</c>, created only when
    /// <see cref="SqliteVecExtensionLoader"/> succeeds. Column names (<c>ChunkId</c>/<c>Embedding</c>)
    /// match the BLOB table exactly, same convention as <see cref="CreateEntryEmbeddingsVecTableAsync"/>.
    /// </summary>
    private static async Task CreateWorkspaceFileEmbeddingsVecTableAsync(
        SqliteConnection connection,
        int dimensions,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand cmd = connection.CreateCommand();

        cmd.CommandText =
            $"""
            CREATE VIRTUAL TABLE IF NOT EXISTS workspace_file_embeddings_vec USING vec0(
                ChunkId TEXT PRIMARY KEY,
                Embedding FLOAT[{dimensions}] distance_metric=cosine
            );
            """;

        _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// RAG Phase 4 — the tables backing Saga (long-term associative memory). Always created
    /// (independent of sqlite-vec availability), mirroring <see cref="CreateWorkspaceFileTablesAsync"/>:
    /// <list type="bullet">
    /// <item><c>saga_memory_embeddings</c>: the BLOB durable store and managed-fallback search source of
    /// truth, keyed by <c>MemoryId</c> (matches <c>saga_memories.Id</c>).</item>
    /// <item><c>saga_memories</c>: memory content and metadata (session, tags, source), joined against
    /// Divination hits to render results. Indexed by <c>SessionId</c> (per-session listing/caps) and
    /// <c>CreatedAt</c> (chronological listing/stats).</item>
    /// <item><c>saga_extraction_watermarks</c>: tracks the most recently extracted Grimoire entry
    /// timestamp per session so <c>SagaExtractionService</c> never re-extracts the same entries.</item>
    /// </list>
    /// </summary>
    private static async Task CreateSagaMemoryTablesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {

        await using SqliteCommand blobCmd = connection.CreateCommand();

        blobCmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS saga_memory_embeddings (
                MemoryId TEXT PRIMARY KEY,
                Embedding BLOB NOT NULL,
                Dim INTEGER NOT NULL
            );
            """;

        _ = await blobCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteCommand memoriesCmd = connection.CreateCommand();

        memoriesCmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS saga_memories (
                Id TEXT PRIMARY KEY,
                Content TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                SessionId TEXT,
                Tags TEXT,
                Source TEXT
            );
            """;

        _ = await memoriesCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteCommand sessionIndexCmd = connection.CreateCommand();

        sessionIndexCmd.CommandText =
            """
            CREATE INDEX IF NOT EXISTS idx_saga_memories_session ON saga_memories(SessionId);
            """;

        _ = await sessionIndexCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteCommand createdIndexCmd = connection.CreateCommand();

        createdIndexCmd.CommandText =
            """
            CREATE INDEX IF NOT EXISTS idx_saga_memories_created ON saga_memories(CreatedAt);
            """;

        _ = await createdIndexCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteCommand watermarksCmd = connection.CreateCommand();

        watermarksCmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS saga_extraction_watermarks (
                SessionId TEXT PRIMARY KEY,
                LastExtractedEntryCreatedAt TEXT NOT NULL
            );
            """;

        _ = await watermarksCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// RAG Phase 4 — vec0 acceleration for <c>saga_memory_embeddings</c>, created only when
    /// <see cref="SqliteVecExtensionLoader"/> succeeds. Column names (<c>MemoryId</c>/<c>Embedding</c>)
    /// match the BLOB table exactly, same convention as <see cref="CreateWorkspaceFileEmbeddingsVecTableAsync"/>.
    /// </summary>
    private static async Task CreateSagaMemoryEmbeddingsVecTableAsync(
        SqliteConnection connection,
        int dimensions,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand cmd = connection.CreateCommand();

        cmd.CommandText =
            $"""
            CREATE VIRTUAL TABLE IF NOT EXISTS saga_memory_embeddings_vec USING vec0(
                MemoryId TEXT PRIMARY KEY,
                Embedding FLOAT[{dimensions}] distance_metric=cosine
            );
            """;

        _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Detects a configured-dimension change against already-imprinted data and logs a warning, for
    /// both <c>entry_embeddings</c> and <c>saga_memory_embeddings</c> — each RAG
    /// feature's BLOB source-of-truth table can independently accumulate stale vectors after a
    /// <c>Arcanum:Integrations:Embeddings:Dimensions</c> change. Deliberately does not auto-truncate: operators must
    /// explicitly clear the affected table(s) (and their <c>_vec</c> counterparts, when present) and
    /// re-index — see <c>docs/Arcanum.DESIGN.md</c> §21.
    /// </summary>
    private static async Task WarnOnDimensionMismatchAsync(
        SqliteConnection connection,
        int configuredDimensions,
        ILogger? logger,
        CancellationToken cancellationToken)
    {

        await WarnOnTableDimensionMismatchAsync(connection, "entry_embeddings", configuredDimensions, logger, cancellationToken)
            .ConfigureAwait(false);

        await WarnOnTableDimensionMismatchAsync(connection, "saga_memory_embeddings", configuredDimensions, logger, cancellationToken)
            .ConfigureAwait(false);

    }

    private static async Task WarnOnTableDimensionMismatchAsync(
        SqliteConnection connection,
        string tableName,
        int configuredDimensions,
        ILogger? logger,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand cmd = connection.CreateCommand();

        // tableName is always one of the small fixed set of internal constants passed above (never
        // user input), matching the trust model already applied to DDL interpolation elsewhere in
        // this file.
        cmd.CommandText = $"""SELECT "Dim" FROM "{tableName}" LIMIT 1;""";

        object? result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (result is null or DBNull)
        {
            return;

        }

        long existingDimensions = Convert.ToInt64(result, CultureInfo.InvariantCulture);

        if (existingDimensions != configuredDimensions)
        {

            logger?.LogWarning(
                "Embedding dimension changed from {OldDimensions} to {NewDimensions} in {TableName}. Existing embeddings are stale. Truncate the embedding tables and re-index to use the new dimension.",
                existingDimensions,
                configuredDimensions,
                tableName);

        }

    }

}
