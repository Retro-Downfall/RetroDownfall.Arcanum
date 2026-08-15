using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// What an install observed about the optional acceleration tier. The durable schema either
/// installed completely or threw, so it needs no reporting.
/// </summary>
internal readonly record struct GrimoireSchemaInstallResult(bool VectorAccelerationAvailable);

/// <summary>
/// Installs the complete Grimoire schema from <see cref="GrimoireSchemaCatalog"/> — AOT-safe, no
/// <c>Database.MigrateAsync</c>, no filesystem access, no migration bookkeeping.
///
/// Two tiers, deliberately different in how they fail:
///
/// <list type="bullet">
/// <item><b>Durable schema</b> (tables + indexes, FTS5 virtual tables, triggers, views) installs in
/// <b>one</b> <see cref="SqliteTransaction"/>. Every statement is <c>IF NOT EXISTS</c>, so this is
/// both a fresh install and a safe re-open; a failure rolls the whole thing back, so a half-built
/// schema can never be observed. The transaction runs under <see cref="SqliteBusyRetry"/> because a
/// concurrent CLI opening the same encrypted file is a busy-wait, not a fault.</item>
/// <item><b>Accelerators</b> (<c>vec0</c> virtual tables) install outside that transaction and only
/// when <see cref="SqliteVecExtensionLoader"/> loads sqlite-vec. Any failure degrades to a logged
/// warning and the managed cosine fallback over the BLOB companion tables, exactly as before — vec0
/// is performance, never correctness (§21.2).</item>
/// </list>
///
/// Callers apply <see cref="SqliteConnectionPragmas"/> to the connection first, so the install runs
/// with <c>journal_mode=WAL</c>, <c>busy_timeout</c>, <c>foreign_keys=ON</c>, and
/// <c>synchronous=NORMAL</c> in force.
/// </summary>
internal static class GrimoireSchemaInstaller
{

    public static async Task<GrimoireSchemaInstallResult> InstallAsync(
        SqliteConnection connection,
        int embeddingDimensions,
        ILogger? logger,
        CancellationToken cancellationToken)
    {

        await SqliteBusyRetry.ExecuteAsync(
            () => InstallObjectsInTransactionAsync(
                connection,
                GrimoireSchemaCatalog.CoreObjects,
                embeddingDimensions,
                cancellationToken),
            cancellationToken).ConfigureAwait(false);

        // No vector acceleration exists. The sqlite-vec probe that used to run here loaded a native
        // extension from the process library search path — the exact behavior the hermetic runtime
        // forbids, and which SQLITE_OMIT_LOAD_EXTENSION now makes impossible. Managed cosine over
        // the BLOB companion tables is the permanent search path until a statically linked
        // accelerator is separately reviewed and shipped.
        const bool vectorAcceleration = false;

        await TryRebuildLexiconFtsAsync(connection, logger, cancellationToken).ConfigureAwait(false);

        await WarnOnDimensionMismatchAsync(connection, embeddingDimensions, logger, cancellationToken)
            .ConfigureAwait(false);

        return new GrimoireSchemaInstallResult(vectorAcceleration);

    }

    /// <summary>
    /// Executes an ordered set of schema objects inside one transaction, rolling back everything on
    /// the first failure. Exposed beyond <see cref="InstallAsync"/> so the atomicity property can be
    /// tested with a deliberately broken object instead of by reflecting into a private method.
    /// </summary>
    internal static async Task InstallObjectsInTransactionAsync(
        SqliteConnection connection,
        IReadOnlyList<GrimoireSchemaObject> definitions,
        int embeddingDimensions,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(definitions);

        if (connection.State != System.Data.ConnectionState.Open)
        {

            throw new InvalidOperationException("Grimoire schema installation requires an open SQLite connection.");

        }

        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {

            foreach (GrimoireSchemaObject definition in definitions)
            {

                cancellationToken.ThrowIfCancellationRequested();

                await using SqliteCommand command = connection.CreateCommand();

                command.Transaction = transaction;

                command.CommandText = GrimoireSchemaCatalog.Resolve(definition, embeddingDimensions);

                _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        }
        catch
        {

            try
            {

                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            }
            catch (Exception)
            {

                // Best-effort — `await using` disposal rolls an uncommitted transaction back anyway.
                // Swallowing here keeps the original failure, the one that says what is actually
                // wrong with the schema, as the exception the caller sees.

            }

            throw;

        }

    }

    /// <summary>
    /// Resynchronizes the Lexicon's FTS5 external-content index with <c>lexicon_entries</c>. A no-op
    /// on the fresh install this schema is designed for, and cheap insurance on a re-open where the
    /// index was dropped or the content table was touched outside the triggers. Best-effort: a
    /// failure narrows Lexicon search until the next rebuild, it does not break the database.
    /// </summary>
    private static async Task TryRebuildLexiconFtsAsync(
        SqliteConnection connection,
        ILogger? logger,
        CancellationToken cancellationToken)
    {

        try
        {

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = "INSERT INTO lexicon_fts(lexicon_fts) VALUES('rebuild');";

            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {

            logger?.LogWarning(
                ex,
                "The Lexicon FTS rebuild after schema install failed; search may be incomplete until the next rebuild.");

        }

    }

    /// <summary>
    /// Detects a configured-dimension change against already-imprinted vectors and logs a warning for
    /// each affected BLOB source-of-truth table. Deliberately never truncates: the operator clears the
    /// affected scope with <c>POST /api/embeddings/reset?confirm=true</c> and re-indexes (§21.2).
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

        try
        {

            await using SqliteCommand command = connection.CreateCommand();

            // tableName is one of the fixed internal constants passed above, never user input — the same
            // trust model the schema object files themselves rely on.
            command.CommandText = $"""SELECT "Dim" FROM "{tableName}" LIMIT 1;""";

            object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            if (result is null or DBNull)
            {

                return;

            }

            long existingDimensions = Convert.ToInt64(result, CultureInfo.InvariantCulture);

            if (existingDimensions != configuredDimensions)
            {

                logger?.LogWarning(
                    "Embedding dimension changed from {OldDimensions} to {NewDimensions} in {TableName}. Existing embeddings are stale. Reset the affected embedding scope and re-index to use the new dimension.",
                    existingDimensions,
                    configuredDimensions,
                    tableName);

            }

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort diagnostic (§5.4.5): a locked database or an older table shape must degrade
            // to a logged warning, never fail the install and abort host startup.
            logger?.LogWarning(
                ex,
                "The embedding dimension probe for {TableName} failed; skipping the dimension-mismatch check.",
                tableName);

        }

    }

}
