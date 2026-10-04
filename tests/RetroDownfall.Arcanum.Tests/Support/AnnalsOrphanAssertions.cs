using System.Data;
using System.Data.Common;
using System.Globalization;

using RetroDownfall.Arcanum.Core.Annals;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// The closing assertion of every erasure-path test: no Annals claim outlives the durable row it
/// describes.
/// </summary>
/// <remarks>
/// A test that counts the claims it expected an erasure to take proves that erasure took those claims.
/// It says nothing about the claim a path took the row from and forgot, because nobody wrote the count
/// for it. This asks the database the question the other way round, over every claim that is there:
/// does its subject row still exist? An erasure path that removes a Saga memory or a Lexicon entry and
/// leaves its claim fails here whatever the test around it chose to count.
///
/// <para>Closed over <see cref="AnnalSubjectStore"/> rather than restated, so a third subject store
/// arrives here as a failure asking which table holds its rows. The comparison normalises both sides
/// because the subject tables do not agree on how an identity is spelled: Saga writes a lowercase
/// dashed identity and the Lexicon dashless hex, and a claim copies whichever its writer was handed.
/// A subject table the database does not install makes every claim of that store an orphan, since
/// nothing is left for those claims to describe.</para>
///
/// <para>The foreign-key check covers what the subject comparison cannot reach: a version, head, edge,
/// review row or historical fact coordinate whose claim or version was taken out from under it.
/// SQLite answers that with <c>PRAGMA foreign_key_check</c> whether or not enforcement is switched on
/// for the connection, so a test that turned it off does not also turn this off.</para>
/// </remarks>
internal static class AnnalsOrphanAssertions
{

    /// <summary>
    /// Fails when any Annals claim names a subject row that is gone, or when any Annals table holds a
    /// row whose foreign key no longer resolves. Returns at once on a database with no Annals.
    /// </summary>
    /// <param name="connection">An open connection with no transaction in progress.</param>
    internal static async Task AssertNoOrphanClaimsAsync(
        DbConnection connection,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(connection);

        if (connection.State != ConnectionState.Open)
        {

            throw new InvalidOperationException(
                "The orphan-claim assertion reads the database it is given and does not open it.");

        }

        if (!await TableExistsAsync(connection, "annal_claims", cancellationToken).ConfigureAwait(false))
        {

            return;

        }

        AnnalSubjectStore[] stores = Enum.GetValues<AnnalSubjectStore>();

        foreach (AnnalSubjectStore store in stores)
        {

            string subject = SubjectTable(store);

            int code = (int)store;

            string sql = await TableExistsAsync(connection, subject, cancellationToken).ConfigureAwait(false)
                ? $"""
                   SELECT count(*) FROM annal_claims c
                   WHERE c.SubjectStoreCode = {code}
                       AND NOT EXISTS (
                           SELECT 1 FROM {subject} s
                           WHERE lower(replace(s.Id, '-', '')) = lower(replace(c.SubjectId, '-', '')));
                   """
                : $"SELECT count(*) FROM annal_claims WHERE SubjectStoreCode = {code};";

            long orphans = await ScalarLongAsync(connection, sql, cancellationToken).ConfigureAwait(false);

            Assert.True(
                orphans == 0,
                $"The Annals subject store '{store}' holds {orphans} claim(s) whose {subject} row is gone.");

        }

        List<string> violations = [];

        foreach (string table in await AnnalsTablesAsync(connection, cancellationToken).ConfigureAwait(false))
        {

            await using DbCommand command = connection.CreateCommand();

            command.CommandText = $"PRAGMA foreign_key_check(\"{table}\");";

            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                string rowId = reader.IsDBNull(1)
                    ? "without a rowid"
                    : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);

                violations.Add(
                    $"{reader.GetString(0)} row {rowId} names a missing {reader.GetString(2)} row "
                    + $"(Annals subject store {OwningStore(reader.GetString(0))})");

            }

        }

        Assert.True(
            violations.Count == 0,
            $"The Annals claim graph holds rows whose foreign key no longer resolves: {string.Join("; ", violations)}.");

    }

    /// <summary>The durable table whose rows a store's claims describe.</summary>
    private static string SubjectTable(AnnalSubjectStore store) =>
        store switch
        {
            AnnalSubjectStore.Saga => "saga_memories",
            AnnalSubjectStore.Lexicon => "lexicon_entries",
            _ => throw new InvalidOperationException($"The Annals subject store '{store}' declares no subject table."),
        };

    /// <summary>
    /// The store a table's rows belong to, where the table says: the Lexicon alone owns its historical
    /// fact coordinates. A row of the shared claim graph names no store of its own, and once its claim is
    /// gone nothing else can name one, so the message says that instead of listing the stores as a
    /// diagnosis.
    /// </summary>
    private static string OwningStore(string table) =>
        table.StartsWith("lexicon_annal_", StringComparison.Ordinal)
            ? nameof(AnnalSubjectStore.Lexicon)
            : "not recoverable once the claim is gone";

    private static async Task<IReadOnlyList<string>> AnnalsTablesAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {

        List<string> tables = [];

        await using DbCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND (name LIKE 'annal\_%' ESCAPE '\' OR name LIKE 'lexicon\_annal\_%' ESCAPE '\')
            ORDER BY name;
            """;

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            tables.Add(reader.GetString(0));

        }

        return tables;

    }

    private static async Task<bool> TableExistsAsync(
        DbConnection connection,
        string table,
        CancellationToken cancellationToken)
    {

        await using DbCommand command = connection.CreateCommand();

        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";

        DbParameter name = command.CreateParameter();

        name.ParameterName = "$name";

        name.Value = table;

        _ = command.Parameters.Add(name);

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) > 0;

    }

    private static async Task<long> ScalarLongAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {

        await using DbCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);

    }

}
