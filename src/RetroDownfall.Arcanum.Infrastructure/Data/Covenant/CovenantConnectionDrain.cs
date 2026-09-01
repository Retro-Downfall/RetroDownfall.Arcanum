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
/// so a best-effort close that reported success over a handle it had failed to close would hand the
/// erasure a lock failure it could only report as a mystery.</para>
///
/// <para>What it proves is that every handle it was shown closed, which is not the same as the
/// process being quiet, and the difference is deliberate. A component this host is running may open
/// a Grimoire connection at any instant — a background reconciliation pass mid-flight, a maintenance
/// sweep between two statements — so a handle correctly closed here and reopened a moment later is
/// an ordinary condition rather than this owner's failure. It is also indistinguishable, in its
/// effect on the erasure, from the reopen that lands a microsecond <i>after</i> this returns, which
/// no drain can forbid: both are the exclusive acquisition's to meet. Refusing over the one that
/// happens to land inside the call would therefore add no protection and would make an operator's
/// erasure succeed or fail on when a background pass ran. Closing that window means quiescing every
/// Grimoire opener for the length of an erasure, which is a larger contract than a connection owner
/// holds and a change that belongs where the host's lifecycle is decided.</para>
/// </remarks>
internal interface ICovenantConnectionDrain
{

    /// <summary>
    /// Enrols one direct handle, until the returned registration is disposed.
    /// </summary>
    IDisposable Register(SqliteConnection connection);

    /// <summary>
    /// Clears the exact pool of one physically closed handle and observes that it remains closed.
    /// </summary>
    Result ClearExactPoolAfterClose(SqliteConnection connection);

    /// <summary>
    /// Closes every enrolled direct handle, clears every idle pool, and refuses over any handle it
    /// could not close.
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
///
/// <para>Each handle is read once, immediately after its own close, and the verify pass judges from
/// that reading rather than from the state alone. Read only at the end, an open handle says nothing
/// about which of two opposite things happened to it: a handle this drain could not close is holding
/// the database and will still be holding it through the exclusive lock that follows, while a handle
/// closed here and reopened by a live component has already been let go of once and is the ordinary
/// traffic of a running host. Enrolment used to cover only handles nobody reopened, so the two never
/// came apart; it now covers every connection Entity Framework opens, and telling them apart is the
/// difference between an erasure that refuses for a reason and one that refuses on timing.</para>
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

    public Result ClearExactPoolAfterClose(SqliteConnection connection)
    {

        ArgumentNullException.ThrowIfNull(connection);

        if (!IsClosed(connection))
        {

            return new Error(
                ErrorCodes.Covenant.MaintenanceFailed,
                "A Grimoire connection must be physically closed before its exact pool is cleared.");

        }

        SqliteConnection.ClearPool(connection);

        if (!IsClosed(connection))
        {

            return new Error(
                ErrorCodes.Covenant.MaintenanceFailed,
                "A Grimoire connection reopened while its exact pool was being cleared.");

        }

        return Result.Success();

    }

    public async Task<Result> DrainAsync(CancellationToken cancellationToken)
    {

        SqliteConnection[] enrolled = Snapshot();

        HashSet<SqliteConnection> closed = [];

        foreach (SqliteConnection handle in enrolled)
        {

            cancellationToken.ThrowIfCancellationRequested();

            try
            {

                await handle.CloseAsync().ConfigureAwait(false);

                if (IsClosed(handle))
                {

                    _ = closed.Add(handle);

                }

            }
            catch (ObjectDisposedException)
            {

                // A handle disposed without unregistering is already released. It is untidy rather
                // than unsafe, and refusing here would block an erasure over bookkeeping.
                _ = closed.Add(handle);

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

            // Open having never been observed closed is the one this owner may not pass on: nothing
            // downstream will let go of it, and the exclusive connection behind it spends a busy
            // timeout per lock before it reports a holder it cannot name. Open after a close read
            // back as closed is a live component that has reopened, which the exclusive acquisition
            // meets exactly as it meets a reopen after this call returns.
            if (!IsClosed(handle) && !closed.Contains(handle))
            {

                return new Error(
                    ErrorCodes.Covenant.MaintenanceFailed,
                    "A Covenant connection handle did not close, so no exclusive maintenance "
                    + "connection may be opened.");

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
