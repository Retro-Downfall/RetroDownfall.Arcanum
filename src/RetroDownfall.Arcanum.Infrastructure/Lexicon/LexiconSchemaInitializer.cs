using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace RetroDownfall.Arcanum.Infrastructure.Lexicon;

/// <summary>
/// Creates The Lexicon's schema idempotently at Grimoire bootstrap, immediately after
/// <c>GrimoireSqlSchemaMigrator.ApplyPendingAsync</c> and alongside <c>WeaveSchemaInitializer</c>
/// (see <c>GrimoireDatabaseBootstrapper.EnsureInitializedAsync</c>). This is intentionally <b>not</b>
/// a registered EF Core migration and <b>not</b> part of the compiled EF model: <c>lexicon_entries</c>
/// and <c>lexicon_fts</c> are accessed entirely via raw SQL through the scoped
/// <c>ArcanumDbContext</c> connection, mirroring <c>SagaMemoryStore</c> / <c>SanctumBreachRepository</c>.
///
/// <c>lexicon_fts</c> is an FTS5 external-content table (<c>content='lexicon_entries'</c>,
/// <c>content_rowid='rowid'</c>) indexing <c>Name</c>, <c>Type</c>, and <c>FactsText</c>. Three
/// triggers (<c>lexicon_entries_ai</c> / <c>_ad</c> / <c>_au</c>) sync the index on insert/delete/update
/// using the FTS5 <c>'delete'</c> command for the old row, so no JSON parsing happens inside SQLite.
/// </summary>
internal static class LexiconSchemaInitializer
{

    public static async Task EnsureSchemaAsync(SqliteConnection connection, ILogger? logger, CancellationToken cancellationToken)
    {

        try
        {

            await CreateEntriesTableAsync(connection, cancellationToken).ConfigureAwait(false);

            await CreateFtsTableAndTriggersAsync(connection, cancellationToken).ConfigureAwait(false);

            await TryRebuildFtsAsync(connection, logger, cancellationToken).ConfigureAwait(false);

        }
        catch (Exception ex)
        {

            // The Lexicon is optional agent memory: a schema problem must never fail Grimoire startup.
            // LexiconService degrades to empty matches / logged write failures when the tables are absent.
            logger?.LogWarning(
                ex,
                "The Lexicon schema initialization failed; Lexicon memory features will be unavailable until this is resolved.");

        }

    }

    private static async Task CreateEntriesTableAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {

        await using SqliteCommand tableCmd = connection.CreateCommand();

        tableCmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS lexicon_entries (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                NameNormalized TEXT NOT NULL,
                Type TEXT NOT NULL,
                FactsJson TEXT NOT NULL,
                FactsText TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            """;

        _ = await tableCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteCommand indexCmd = connection.CreateCommand();

        indexCmd.CommandText =
            """
            CREATE UNIQUE INDEX IF NOT EXISTS IX_lexicon_entries_NameNormalized
            ON lexicon_entries(NameNormalized);
            """;

        _ = await indexCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static async Task CreateFtsTableAndTriggersAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {

        await using SqliteCommand ftsCmd = connection.CreateCommand();

        ftsCmd.CommandText =
            """
            CREATE VIRTUAL TABLE IF NOT EXISTS lexicon_fts USING fts5(
                Name,
                Type,
                FactsText,
                content='lexicon_entries',
                content_rowid='rowid'
            );
            """;

        _ = await ftsCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteCommand triggerCmd = connection.CreateCommand();

        triggerCmd.CommandText =
            """
            CREATE TRIGGER IF NOT EXISTS lexicon_entries_ai
            AFTER INSERT ON lexicon_entries
            BEGIN
                INSERT INTO lexicon_fts(rowid, Name, Type, FactsText)
                VALUES (new.rowid, new.Name, new.Type, new.FactsText);
            END;

            CREATE TRIGGER IF NOT EXISTS lexicon_entries_ad
            AFTER DELETE ON lexicon_entries
            BEGIN
                INSERT INTO lexicon_fts(lexicon_fts, rowid, Name, Type, FactsText)
                VALUES ('delete', old.rowid, old.Name, old.Type, old.FactsText);
            END;

            CREATE TRIGGER IF NOT EXISTS lexicon_entries_au
            AFTER UPDATE ON lexicon_entries
            BEGIN
                INSERT INTO lexicon_fts(lexicon_fts, rowid, Name, Type, FactsText)
                VALUES ('delete', old.rowid, old.Name, old.Type, old.FactsText);

                INSERT INTO lexicon_fts(rowid, Name, Type, FactsText)
                VALUES (new.rowid, new.Name, new.Type, new.FactsText);
            END;
            """;

        _ = await triggerCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static async Task TryRebuildFtsAsync(SqliteConnection connection, ILogger? logger, CancellationToken cancellationToken)
    {

        try
        {

            await using SqliteCommand rebuildCmd = connection.CreateCommand();

            rebuildCmd.CommandText = "INSERT INTO lexicon_fts(lexicon_fts) VALUES('rebuild');";

            _ = await rebuildCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }
        catch (Exception ex)
        {

            logger?.LogWarning(
                ex,
                "The Lexicon FTS rebuild after schema create failed; search may be incomplete until the next rebuild.");

        }

    }

}
