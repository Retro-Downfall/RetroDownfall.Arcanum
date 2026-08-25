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

    /// <summary>
    /// The same open connection, for a statement that reads only always-present core tables.
    /// </summary>
    /// <remarks>
    /// Deliberately does not latch <see cref="CovenantProcessResidence"/>. That latch is one-way and
    /// exists to forbid the offline host-tools transition once this process has held Covenant
    /// material; a statement over a table no Covenant capability owns has held none. Latching for
    /// one would close the transition on a gate-off installation whose canonical tier was never
    /// opened, and the operator would have no way to reopen it.
    /// </remarks>
    ValueTask<SqliteConnection> GetOpenCoreConnectionAsync(CancellationToken cancellationToken);

}

/// <summary>
/// The ordinary source: the scoped Grimoire context's own connection.
/// </summary>
internal sealed class CovenantConnectionSource(ArcanumDbContext db) : ICovenantConnectionSource
{

    public ValueTask<SqliteConnection> GetOpenConnectionAsync(CancellationToken cancellationToken)
    {

        // The one choke point every canonical read and write passes through, and therefore the one
        // place that can honestly latch "this process has held Covenant material".
        CovenantProcessResidence.MarkOpened();

        return GetOpenCoreConnectionAsync(cancellationToken);

    }

    public async ValueTask<SqliteConnection> GetOpenCoreConnectionAsync(CancellationToken cancellationToken)
    {

        if (db.Database.GetDbConnection() is not SqliteConnection connection)
        {

            throw new InvalidOperationException(
                "The Covenant canonical tier requires a SQLCipher connection.");

        }

        if (connection.State != System.Data.ConnectionState.Open)
        {

            // Through EF rather than the raw connection, because opening the raw handle skips
            // RelationalConnection and therefore skips the pragma interceptor — the only thing that
            // registers a connection with CovenantSqliteConnectionInitializer. Every privileged
            // Covenant statement authorizes against that registration, so a connection opened raw is
            // refused by its own initializer the moment it is used. A live request never saw this
            // because EF had already opened the connection loading the turn, leaving this branch
            // unreached; a scope that does no EF work first — a background sweep — hits it every time.
            // Every other service in this repository opens the Grimoire this way for the same reason.
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        }

        return connection;

    }

}
