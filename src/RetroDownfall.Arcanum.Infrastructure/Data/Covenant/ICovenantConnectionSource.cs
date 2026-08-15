using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// Hands a Covenant component the one open, centrally initialized Grimoire connection it may use.
/// </summary>
/// <remarks>
/// A seam rather than a direct <see cref="ArcanumDbContext"/> dependency, because the canonical tier
/// is also reached from exclusive maintenance paths that own their own connection and from suites
/// that install a scratch tier. Every implementation must return a connection that has already been
/// through <see cref="CovenantSqliteConnectionInitializer"/>: the canonical triggers call
/// authorization functions that only exist on an initialized connection.
/// </remarks>
internal interface ICovenantConnectionSource
{

    ValueTask<SqliteConnection> GetOpenConnectionAsync(CancellationToken cancellationToken);

}

/// <summary>
/// The ordinary source: the scoped Grimoire context's own connection.
/// </summary>
internal sealed class CovenantConnectionSource(ArcanumDbContext db) : ICovenantConnectionSource
{

    public async ValueTask<SqliteConnection> GetOpenConnectionAsync(CancellationToken cancellationToken)
    {

        if (db.Database.GetDbConnection() is not SqliteConnection connection)
        {

            throw new InvalidOperationException(
                "The Covenant canonical tier requires a SQLCipher connection.");

        }

        if (connection.State != System.Data.ConnectionState.Open)
        {

            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        }

        return connection;

    }

}
