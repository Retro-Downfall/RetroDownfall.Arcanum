using System.Data;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// The central connection owner's drain: the one way a Covenant maintenance path clears every idle
/// pool and closes every direct handle before it takes an exclusive lock.
/// </summary>
/// <remarks>
/// A contract rather than a bare <c>SqliteConnection.ClearAllPools()</c> at each call site, because
/// a pool clear on its own answers the wrong question. It empties the idle pools and says nothing
/// about the handles this process is holding outside one — the warm disclosure writer's lease, a
/// diagnostics reader, a maintenance connection an earlier phase opened — and an exclusive
/// maintenance connection opened while any of those is still live fails on a lock whose holder the
/// caller cannot name (§10.20.5).
///
/// <para>Ownership is by enrolment. A component that opens a direct handle registers it here and
/// unregisters on dispose, so the drain closes exactly the handles this process actually holds
/// rather than a list somebody remembered to keep up to date.</para>
///
/// <para>The drain proves its own result. Its caller's next act is an exclusive lock and a delete,
/// so a best-effort close that reported success over a surviving handle would hand the erasure a
/// lock failure it could only report as a mystery.</para>
/// </remarks>
internal interface ICovenantConnectionDrain
{

    /// <summary>
    /// Enrols one direct handle, until the returned registration is disposed.
    /// </summary>
    IDisposable Register(SqliteConnection connection);

    /// <summary>
    /// Closes every enrolled direct handle, clears every idle pool, and proves nothing survived.
    /// </summary>
    Task<Result> DrainAsync(CancellationToken cancellationToken);

}

/// <summary>
/// The process-wide drain, holding the enrolment set every Covenant maintenance path drains through.
/// </summary>
/// <remarks>
/// The order is the whole of the implementation and it is not interchangeable: direct handles close
/// first, and only then are the pools cleared. Closing a <i>pooled</i> connection returns its native
/// handle to the pool rather than releasing it, so a drain that cleared the pools first would leave
/// every handle it closed afterwards sitting idle in a pool it had already emptied.
///
/// <para>Enrolments are counted rather than pooled into a set. The same handle is enrolled by more
/// than one component — the connection Entity Framework opens is enrolled at the open and again by
/// the Covenant connection source that hands it out — and with a set the first release would drop
/// the other component's registration too, leaving an open handle outside the drain until whichever
/// component still believed it was enrolled let go.</para>
/// </remarks>
internal sealed class CovenantConnectionDrain : ICovenantConnectionDrain
{

    private readonly Lock _gate = new();

    private readonly Dictionary<SqliteConnection, int> _handles = [];

    public IDisposable Register(SqliteConnection connection)
    {

        ArgumentNullException.ThrowIfNull(connection);

        lock (_gate)
        {

            _handles[connection] = _handles.TryGetValue(connection, out int enrolments)
                ? enrolments + 1
                : 1;

        }

        return new Enrolment(this, connection);

    }

    public async Task<Result> DrainAsync(CancellationToken cancellationToken)
    {

        SqliteConnection[] enrolled = Snapshot();

        foreach (SqliteConnection handle in enrolled)
        {

            cancellationToken.ThrowIfCancellationRequested();

            try
            {

                await handle.CloseAsync().ConfigureAwait(false);

            }
            catch (ObjectDisposedException)
            {

                // A handle disposed without unregistering is already released. It is untidy rather
                // than unsafe, and refusing here would block an erasure over bookkeeping.

            }
            catch (SqliteException failed)
            {

                return new Error(
                    ErrorCodes.Covenant.MaintenanceFailed,
                    $"A Covenant connection handle did not close: {failed.Message}");

            }

        }

        // After the direct handles, never before: see the type remarks.
        SqliteConnection.ClearAllPools();

        foreach (SqliteConnection handle in enrolled)
        {

            if (!IsClosed(handle))
            {

                return new Error(
                    ErrorCodes.Covenant.MaintenanceFailed,
                    "A Covenant connection handle is still open after the drain, so no exclusive "
                    + "maintenance connection may be opened.");

            }

        }

        return Result.Success();

    }

    private static bool IsClosed(SqliteConnection handle)
    {

        try
        {

            return handle.State == ConnectionState.Closed;

        }
        catch (ObjectDisposedException)
        {

            return true;

        }

    }

    private SqliteConnection[] Snapshot()
    {

        lock (_gate)
        {

            return [.. _handles.Keys];

        }

    }

    private void Unregister(SqliteConnection connection)
    {

        lock (_gate)
        {

            if (!_handles.TryGetValue(connection, out int enrolments))
            {

                return;

            }

            if (enrolments > 1)
            {

                _handles[connection] = enrolments - 1;

                return;

            }

            _ = _handles.Remove(connection);

        }

    }

    /// <summary>
    /// One handle's enrolment. Disposal is idempotent so a component with both a <c>using</c> and an
    /// explicit release pays back one enrolment rather than two, which would otherwise cancel a
    /// second component's registration of the same handle.
    /// </summary>
    private sealed class Enrolment(CovenantConnectionDrain owner, SqliteConnection connection) : IDisposable
    {

        private bool _disposed;

        public void Dispose()
        {

            if (_disposed)
            {

                return;

            }

            _disposed = true;

            owner.Unregister(connection);

        }

    }

}
