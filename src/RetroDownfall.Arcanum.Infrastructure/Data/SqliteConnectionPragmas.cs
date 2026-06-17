using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal static class SqliteConnectionPragmas
{

    public const int BusyTimeoutMs = 5000;

    public static async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            PRAGMA foreign_keys=ON;
            """;

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static void Apply(DbConnection connection)
    {

        if (connection is not SqliteConnection sqlite)
        {

            return;

        }

        using SqliteCommand command = sqlite.CreateCommand();

        command.CommandText =
            """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            PRAGMA foreign_keys=ON;
            """;

        _ = command.ExecuteNonQuery();

    }

}
