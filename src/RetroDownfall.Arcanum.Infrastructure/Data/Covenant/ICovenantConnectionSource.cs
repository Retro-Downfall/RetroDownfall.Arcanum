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
/// <remarks>
/// This type opens the scope's connection and never closes it, so the handle it hands out is held for
/// the life of the scope rather than the life of a statement. That is what makes enrolment its
/// responsibility rather than somebody else's: an open handle nothing enrolled is invisible to the
/// drain, survives the pool clear because it is in use rather than idle, and costs the exclusive
/// maintenance connection that follows one busy timeout per wal-index lock — tens of seconds of
/// waiting ending in <c>database is locked</c>, with no way for that caller to name the holder.
///
/// <para>The same connection is also enrolled by <c>LongRunningOperationStore</c>, in the scopes that
/// resolve one. Enrolling twice is harmless because both are scoped to the connection they enrol and
/// are disposed with it, and relying on the store's enrolment was the defect: whether a held Covenant
/// handle was drained depended on which unrelated service the scope happened to resolve.</para>
/// </remarks>
internal sealed class CovenantConnectionSource : ICovenantConnectionSource, IDisposable
{

    private readonly ArcanumDbContext _db;

    private IDisposable? _enrolment;

    internal CovenantConnectionSource(ArcanumDbContext db, ICovenantConnectionDrain drain)
    {

        ArgumentNullException.ThrowIfNull(db);

        ArgumentNullException.ThrowIfNull(drain);

        _db = db;

        // Enrolled before the first open, not on it. A handle enrolled only once somebody asked for it
        // would leave the window between the context opening its own connection and the first Covenant
        // read invisible to the drain, and that window is exactly where an erasure runs.
        _enrolment = db.Database.GetDbConnection() is SqliteConnection sqlite
            ? drain.Register(sqlite)
            : null;

    }

    /// <summary>Releases this scope's handle from the process-wide Covenant drain.</summary>
    public void Dispose() =>
        Interlocked.Exchange(ref _enrolment, null)?.Dispose();

    public ValueTask<SqliteConnection> GetOpenConnectionAsync(CancellationToken cancellationToken)
    {

        // The one choke point every canonical read and write passes through, and therefore the one
        // place that can honestly latch "this process has held Covenant material".
        CovenantProcessResidence.MarkOpened();

        return GetOpenCoreConnectionAsync(cancellationToken);

    }

    public async ValueTask<SqliteConnection> GetOpenCoreConnectionAsync(CancellationToken cancellationToken)
    {

        if (_db.Database.GetDbConnection() is not SqliteConnection connection)
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
