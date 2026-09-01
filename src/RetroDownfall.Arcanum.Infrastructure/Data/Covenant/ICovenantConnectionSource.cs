using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Primitives;

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
/// This type acquires the scope's connection once and retains that ordinary admission lease for the
/// scope lifetime. Downstream Covenant components keep the existing bare-connection contract, while
/// the source remains the one owner that cannot accidentally discard the admission or drain lifetime
/// before the last scoped statement finishes.
/// </remarks>
internal sealed class CovenantConnectionSource : ICovenantConnectionSource, IDisposable
{

    private readonly ArcanumDbContext _db;

    private readonly IGrimoireOrdinaryConnectionFactory _connections;

    private readonly SemaphoreSlim _leaseGate = new(1, 1);

    private IGrimoireOrdinaryConnectionLease? _lease;

    private int _disposed;

    internal CovenantConnectionSource(
        ArcanumDbContext db,
        IGrimoireOrdinaryConnectionFactory connections)
    {

        ArgumentNullException.ThrowIfNull(db);

        ArgumentNullException.ThrowIfNull(connections);

        _db = db;

        _connections = connections;

    }

    /// <summary>Releases this scope's handle from the process-wide Covenant drain.</summary>
    public void Dispose()
    {

        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {

            return;

        }

        Interlocked.Exchange(ref _lease, null)?.Dispose();

        _leaseGate.Dispose();

    }

    public ValueTask<SqliteConnection> GetOpenConnectionAsync(CancellationToken cancellationToken)
    {

        // The one choke point every canonical read and write passes through, and therefore the one
        // place that can honestly latch "this process has held Covenant material".
        CovenantProcessResidence.MarkOpened();

        return GetOpenCoreConnectionAsync(cancellationToken);

    }

    public async ValueTask<SqliteConnection> GetOpenCoreConnectionAsync(CancellationToken cancellationToken)
    {

        IGrimoireOrdinaryConnectionLease? retained = Volatile.Read(ref _lease);

        if (retained is not null)
        {

            return retained.Connection;

        }

        if (_db.Database.GetDbConnection() is not SqliteConnection connection)
        {

            throw new InvalidOperationException(
                "The Covenant canonical tier requires a SQLCipher connection.");

        }

        await _leaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {

            retained = _lease;

            if (retained is not null)
            {

                return retained.Connection;

            }

            Result<IGrimoireOrdinaryConnectionLease> acquired = await _connections
                .AcquireScopedAsync(
                    connection,
                    CovenantSqliteConnectionMode.ReadWrite,
                    cancellationToken)
                .ConfigureAwait(false);

            if (acquired.IsFailure)
            {

                throw new GrimoireMaintenanceUnavailableException();

            }

            _lease = acquired.Value;

            return acquired.Value.Connection;

        }
        finally
        {

            _leaseGate.Release();

        }

    }

}
