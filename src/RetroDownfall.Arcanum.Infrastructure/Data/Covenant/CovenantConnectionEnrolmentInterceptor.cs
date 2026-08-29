using System.Data.Common;
using System.Runtime.CompilerServices;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// Enrols every Grimoire connection Entity Framework opens with the Covenant drain, for exactly as
/// long as it is open.
/// </summary>
/// <remarks>
/// Enrolment used to be a component's responsibility — the Covenant connection source enrolled the
/// scope's handle, and so did the long-running operation store — which made "is this open handle
/// drained" a question about which unrelated services the scope happened to resolve. A scope that
/// obtained <see cref="ArcanumDbContext"/> and opened its connection without asking for either
/// component held the Grimoire open and was invisible to the drain, and the maintenance sweep driver
/// is exactly that scope. An unenrolled handle survives both the drain and the pool clear, because it
/// is in use rather than idle, and the exclusive maintenance connection that follows then spends the
/// whole busy timeout on every wal-index lock its first transaction takes before <c>BEGIN</c> gives
/// up: tens of seconds of waiting ending in <c>database is locked</c>, with no way for that caller to
/// name the holder.
///
/// <para>Hooked to the open rather than to a constructor, so opening without enrolling is not a shape
/// this composition can express. A constructor-time enrolment would also be wrong for a pooled
/// context, whose constructor runs once per pooled instance while its connection is opened and closed
/// many times after that.</para>
///
/// <para>Enrolment is released on close and on dispose, and holding it across a close would be the
/// safer-looking mistake: the drain keeps a strong reference to every handle it is holding, since an
/// open connection nobody references still holds the database file, so a registration that outlived
/// its connection would keep that connection alive for the life of the process.</para>
/// </remarks>
internal sealed class CovenantConnectionEnrolmentInterceptor : DbConnectionInterceptor
{

    private readonly ICovenantConnectionDrain _drain;

    private readonly Lock _gate = new();

    private readonly ConditionalWeakTable<DbConnection, IDisposable> _enrolments = [];

    internal CovenantConnectionEnrolmentInterceptor(ICovenantConnectionDrain drain)
    {

        ArgumentNullException.ThrowIfNull(drain);

        _drain = drain;

    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {

        Enrol(connection);

        base.ConnectionOpened(connection, eventData);

    }

    public override Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {

        Enrol(connection);

        return base.ConnectionOpenedAsync(connection, eventData, cancellationToken);

    }

    public override void ConnectionClosed(DbConnection connection, ConnectionEndEventData eventData)
    {

        Release(connection);

        base.ConnectionClosed(connection, eventData);

    }

    public override Task ConnectionClosedAsync(DbConnection connection, ConnectionEndEventData eventData)
    {

        Release(connection);

        return base.ConnectionClosedAsync(connection, eventData);

    }

    public override void ConnectionDisposed(DbConnection connection, ConnectionEndEventData eventData)
    {

        Release(connection);

        base.ConnectionDisposed(connection, eventData);

    }

    public override Task ConnectionDisposedAsync(DbConnection connection, ConnectionEndEventData eventData)
    {

        Release(connection);

        return base.ConnectionDisposedAsync(connection, eventData);

    }

    /// <summary>
    /// Enrols one handle once, however many times its owner reopens it.
    /// </summary>
    /// <remarks>
    /// A drain closes the handles it holds directly rather than through Entity Framework, so a
    /// reopen after a drain arrives here with the enrolment still standing. Registering again would
    /// leave a count this interceptor can never pay back, and the handle would stay enrolled after
    /// its owner had let go of it.
    /// </remarks>
    private void Enrol(DbConnection connection)
    {

        if (connection is not SqliteConnection sqlite)
        {

            return;

        }

        lock (_gate)
        {

            if (_enrolments.TryGetValue(sqlite, out _))
            {

                return;

            }

            _enrolments.Add(sqlite, _drain.Register(sqlite));

        }

    }

    private void Release(DbConnection connection)
    {

        lock (_gate)
        {

            if (!_enrolments.TryGetValue(connection, out IDisposable? enrolment))
            {

                return;

            }

            _ = _enrolments.Remove(connection);

            enrolment.Dispose();

        }

    }

}
