using System.Globalization;
using System.Text;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// Every erasure evidence row and the erasure key's stored secret, as they stood in one read snapshot.
/// </summary>
/// <remarks>
/// Each row is rendered column by column, in primary-key order: a blob as hex, an integer in invariant
/// digits, text as stored and a null as <c>NULL</c>. Two snapshots are equal only when every row of all
/// three tables is byte-identical, so a lifecycle path that rewrote a receipt in place, or deleted one row
/// and inserted another with the same count, is caught as surely as one that deleted a row.
/// </remarks>
internal sealed record MemoryErasureRetainedSnapshot(
    IReadOnlyList<string> Fingerprints,
    IReadOnlyList<string> Receipts,
    IReadOnlyList<string> Subjects,
    string? KeySecret);

/// <summary>Captures erasure evidence and the erasure key, and proves both outlived a lifecycle path.</summary>
/// <remarks>
/// The caller passes a connection of its own: in a hosted suite a fresh read-only lease from the host's
/// ordinary connection factory, so the read is the host's own view of the Grimoire and holds nothing a
/// lifecycle path could wait on. The three tables are read inside one <c>BEGIN</c> snapshot and the key from
/// the test's in-memory credential store, never the OS keychain.
/// </remarks>
internal static class MemoryErasureRetainedEvidence
{
    internal static async Task<MemoryErasureRetainedSnapshot> CaptureAsync(
        SqliteConnection connection,
        InMemoryOsCredentialStore credentials,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(credentials);

        await ExecuteAsync(connection, "BEGIN;", cancellationToken);

        try
        {
            IReadOnlyList<string> fingerprints = await RowsAsync(
                connection,
                "SELECT * FROM memory_erasure_fingerprints ORDER BY Fingerprint;",
                cancellationToken);

            IReadOnlyList<string> receipts = await RowsAsync(
                connection,
                "SELECT * FROM memory_erasure_receipts ORDER BY MutationId;",
                cancellationToken);

            IReadOnlyList<string> subjects = await RowsAsync(
                connection,
                "SELECT * FROM memory_erasure_receipt_subjects ORDER BY MutationId, SubjectDigest;",
                cancellationToken);

            OsCredentialStoreResult key = credentials.TryGet(
                ArcanumCredentialIdentity.Service,
                ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount);

            return new(
                fingerprints,
                receipts,
                subjects,
                key.Status == OsCredentialStoreStatus.Ok ? key.Value : null);
        }
        finally
        {
            await ExecuteAsync(connection, "ROLLBACK;", CancellationToken.None);
        }
    }

    /// <summary>
    /// Reads the evidence and the key again and requires both to equal <paramref name="before"/> exactly.
    /// </summary>
    internal static async Task AssertRetainedAsync(
        MemoryErasureRetainedSnapshot before,
        SqliteConnection connection,
        InMemoryOsCredentialStore credentials,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(before);

        MemoryErasureRetainedSnapshot after = await CaptureAsync(connection, credentials, cancellationToken);

        Assert.Equal(before.Fingerprints, after.Fingerprints);

        Assert.Equal(before.Receipts, after.Receipts);

        Assert.Equal(before.Subjects, after.Subjects);

        Assert.Equal(before.KeySecret, after.KeySecret);
    }

    private static async Task<IReadOnlyList<string>> RowsAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        List<string> rows = [];

        while (await reader.ReadAsync(cancellationToken))
        {
            StringBuilder row = new();

            for (int column = 0; column < reader.FieldCount; column++)
            {
                if (column > 0)
                {
                    row.Append('|');
                }

                row.Append(reader.GetName(column)).Append('=').Append(Render(reader.GetValue(column)));
            }

            rows.Add(row.ToString());
        }

        return rows;
    }

    private static string Render(object value) =>
        value switch
        {
            DBNull => "NULL",
            byte[] bytes => "x'" + Convert.ToHexString(bytes) + "'",
            long integer => integer.ToString(CultureInfo.InvariantCulture),
            double real => real.ToString("R", CultureInfo.InvariantCulture),
            string text => "'" + text + "'",
            _ => throw new InvalidOperationException($"An evidence column held an unexpected {value.GetType().Name}."),
        };

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
