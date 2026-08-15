using System.Data.Common;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Compatibility facade over <see cref="CovenantSqliteConnectionInitializer" />.
/// </summary>
/// <remarks>
/// Kept so existing call sites keep compiling, but it no longer contains policy of its own: every
/// call routes to the one initializer, which also registers the authorization functions and reads
/// the applied policy back. Prefer taking
/// <see cref="ICovenantSqliteConnectionInitializer" /> and naming an explicit
/// <see cref="CovenantSqliteConnectionMode" />.
/// </remarks>
internal static class SqliteConnectionPragmas
{

    public const int BusyTimeoutMs = CovenantSqliteConnectionInitializer.BusyTimeoutMs;

    public static async Task ApplyAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken = default) =>
        await CovenantSqliteConnectionInitializer.Instance
            .InitializeAsync(connection, CovenantSqliteConnectionMode.ReadWrite, cancellationToken)
            .ConfigureAwait(false);

    public static void Apply(DbConnection connection)
    {

        if (connection is not SqliteConnection sqlite)
        {

            return;

        }

        // The synchronous EF callback has no asynchronous continuation to await into; the work is
        // a handful of pragmas against an already-open local handle.
        CovenantSqliteConnectionInitializer.Instance
            .InitializeAsync(sqlite, CovenantSqliteConnectionMode.ReadWrite, CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();

    }

}
