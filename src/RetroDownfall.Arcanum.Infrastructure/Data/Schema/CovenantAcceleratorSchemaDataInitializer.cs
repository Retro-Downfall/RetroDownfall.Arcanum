using System.Globalization;
using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// Turns on FTS5 secure delete and proves rank-1 integrity inside the Covenant accelerator install
/// transaction.
/// </summary>
/// <remarks>
/// Secure delete matters here specifically: the accelerator is the one Covenant object that holds
/// plaintext-derived tokens in its own pages. Without it, a deleted token stays legible in a freed
/// page until that page is reused, which would let retired Covenant content survive its retirement
/// inside the search index.
///
/// <para>Both commands and the read-back throw on failure, which is the point. This initializer runs
/// inside the accelerator transaction, so a runtime that cannot honor secure delete or that reports
/// a corrupt index rolls back <i>only</i> this tier: canonical stays committed and inspection search
/// degrades to the canonical fallback.</para>
/// </remarks>
internal sealed class CovenantAcceleratorSchemaDataInitializer : IGrimoireSchemaDataInitializer
{

    public GrimoireSchemaTransactionTier TransactionTier =>
        GrimoireSchemaTransactionTier.CovenantAccelerator;

    public async Task InitializeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GrimoireSchemaInitializationContext context,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(transaction);

        ArgumentNullException.ThrowIfNull(context);

        await ExecuteAsync(
            connection,
            transaction,
            "INSERT INTO covenant_fts(covenant_fts, rank) VALUES('secure-delete', 1);",
            cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            transaction,
            "INSERT INTO covenant_fts(covenant_fts, rank) VALUES('integrity-check', 1);",
            cancellationToken).ConfigureAwait(false);

        long secureDelete = await ReadConfigurationAsync(
            connection,
            transaction,
            "secure-delete",
            cancellationToken).ConfigureAwait(false);

        if (secureDelete != 1)
        {

            throw new InvalidOperationException(
                "The Covenant FTS5 index did not report secure-delete after it was enabled.");

        }

    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static async Task<long> ReadConfigurationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        // The shadow config table is the only place FTS5 exposes an applied rank setting for
        // read-back; there is no pragma equivalent.
        command.CommandText = "SELECT v FROM covenant_fts_config WHERE k = $key;";

        _ = command.Parameters.AddWithValue("$key", key);

        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is null or DBNull
            ? 0
            : Convert.ToInt64(result, CultureInfo.InvariantCulture);

    }

}
