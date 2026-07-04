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
/// <item>There is no existing data to migrate (net-new feature, no users yet), so the append-only,
/// timestamped migration convention — built for evolving a live schema under existing rows — is
/// unnecessary ceremony here.</item>
/// <item>The vec0 table's vector column width must be interpolated from the configured
/// <c>Arcanum:Embeddings:Dimensions</c> at runtime; a static embedded <c>.sql</c> migration file
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

            bool vecAvailable = SqliteVecExtensionLoader.TryLoad(connection, logger);

            availability.SetAvailable(vecAvailable);

            if (vecAvailable)
            {

                await CreateEntryEmbeddingsVecTableAsync(connection, configuredDimensions, cancellationToken)
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
    /// Detects a configured-dimension change against already-imprinted data and logs a warning.
    /// Deliberately does not auto-truncate: operators must explicitly clear <c>entry_embeddings</c> (and
    /// <c>entry_embeddings_vec</c>, when present) and re-index — see DESIGN.md §21.
    /// </summary>
    private static async Task WarnOnDimensionMismatchAsync(
        SqliteConnection connection,
        int configuredDimensions,
        ILogger? logger,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand cmd = connection.CreateCommand();

        cmd.CommandText =
            """
            SELECT "Dim" FROM "entry_embeddings" LIMIT 1;
            """;

        object? result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (result is null or DBNull)
        {
            return;

        }

        long existingDimensions = Convert.ToInt64(result, CultureInfo.InvariantCulture);

        if (existingDimensions != configuredDimensions)
        {

            logger?.LogWarning(
                "Embedding dimension changed from {OldDimensions} to {NewDimensions}. Existing embeddings are stale. Truncate the embedding tables and re-index to use the new dimension.",
                existingDimensions,
                configuredDimensions);

        }

    }

}
