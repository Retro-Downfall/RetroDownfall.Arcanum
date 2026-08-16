using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// Identity of the schema actually installed in a Grimoire database, derived from
/// <c>sqlite_master</c> itself. This replaces the <c>__EFMigrationsHistory</c> row that used to
/// answer "which schema is this?" — with no migration chain there is no bookkeeping table to read,
/// and a hash over the real catalog is strictly better anyway: it changes when the schema changes,
/// including drift a history row would have happily lied about.
///
/// Used by <c>.arcbackup</c>: <c>BackupService</c> records the identity of the consistent snapshot
/// it captured, and archive verification recomputes it against the extracted snapshot so a mismatch
/// is reported as <c>backup.database_schema_mismatch</c> (§5.4.8).
///
/// It is deliberately <b>not</b> enforced at startup. Every schema object installs with
/// <c>IF NOT EXISTS</c>, so opening an older local database still works; an incompatible one is
/// recreated under the pre-user-data reinstall policy at the operator's discretion, not refused by
/// the host (<c>README.md</c>, "Local Grimoire reinstall").
/// </summary>
internal static class GrimoireSchemaIdentity
{

    /// <summary>Prefix that makes the recorded value self-describing rather than a bare hash.</summary>
    internal const string Prefix = "sha256-";

    // ASCII unit/record separators. Neither can appear in a SQLite object name or in the DDL text
    // SQLite stores, so no combination of catalog fields can be framed into a colliding string.
    private const char FieldSeparator = '\u001F';

    private const char RowSeparator = '\u001E';

    /// <summary>
    /// Hashes every <c>sqlite_master</c> row — object type, name, owning table, and the stored DDL
    /// text — in a deterministic order. Optional <c>vec0</c> tables and FTS5 shadow tables are
    /// included on purpose: they are part of what the file actually contains, and backup verification
    /// compares one snapshot against itself rather than across hosts.
    /// </summary>
    public static async Task<string> ComputeAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT "type", "name", "tbl_name", COALESCE("sql", '')
            FROM sqlite_master
            ORDER BY "type", "name", "tbl_name";
            """;

        StringBuilder catalog = new();

        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                _ = catalog
                    .Append(reader.GetString(0))
                    .Append(FieldSeparator)
                    .Append(reader.GetString(1))
                    .Append(FieldSeparator)
                    .Append(reader.GetString(2))
                    .Append(FieldSeparator)
                    .Append(reader.GetString(3))
                    .Append(RowSeparator);

            }

        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(catalog.ToString()));

        return Prefix + Convert.ToHexStringLower(hash);

    }

}
