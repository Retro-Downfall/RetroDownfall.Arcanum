using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// Owns ordinary Grimoire admission and Covenant-drain enrolment for every physical connection
/// Entity Framework opens.
/// </summary>
/// <remarks>
/// Admission is acquired before provider I/O and revalidated after it. The post-open ordering is
/// deliberate: a handle that loses its generation is physically closed before its ticket reports
/// refusal, while a successfully revalidated handle is enrolled in the drain before EF can return
/// it to its caller.
///
/// <para>One weak lifecycle state follows each physical connection across pooled reopen cycles. Its
/// drain registration is only this interceptor's registration; the drain itself remains reference
/// counted, so releasing it cannot unregister a second logical holder of the same handle.</para>
/// </remarks>
internal sealed class CovenantConnectionEnrolmentInterceptor : DbConnectionInterceptor
{

    private readonly IGrimoireConnectionAdmissionGate _admissionGate;

    private readonly ICovenantConnectionDrain _drain;

    private readonly Lock _gate = new();

    private readonly ConditionalWeakTable<DbConnection, ConnectionLifecycleState> _lifecycles = [];

    internal CovenantConnectionEnrolmentInterceptor(
        IGrimoireConnectionAdmissionGate admissionGate,
        ICovenantConnectionDrain drain)
    {

        ArgumentNullException.ThrowIfNull(admissionGate);

        ArgumentNullException.ThrowIfNull(drain);

        _admissionGate = admissionGate;

        _drain = drain;

    }

    public override InterceptionResult ConnectionOpening(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result)
    {

        BeginOpen(connection);

        return base.ConnectionOpening(connection, eventData, result);

    }

    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {

        BeginOpen(connection);

        return base.ConnectionOpeningAsync(
            connection,
            eventData,
            result,
            cancellationToken);

    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {

        if (!CompleteOpen(connection))
        {

            connection.Close();

            CompleteRefusalAfterPhysicalClose(connection);

            throw new GrimoireMaintenanceUnavailableException();

        }

        base.ConnectionOpened(connection, eventData);

    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {

        if (!CompleteOpen(connection))
        {

            await connection.CloseAsync().ConfigureAwait(false);

            CompleteRefusalAfterPhysicalClose(connection);

            throw new GrimoireMaintenanceUnavailableException();

        }

        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken)
            .ConfigureAwait(false);

    }

    public override void ConnectionFailed(
        DbConnection connection,
        ConnectionErrorEventData eventData)
    {

        Release(connection);

        base.ConnectionFailed(connection, eventData);

    }

    public override Task ConnectionFailedAsync(
        DbConnection connection,
        ConnectionErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {

        Release(connection);

        return base.ConnectionFailedAsync(connection, eventData, cancellationToken);

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

    private void BeginOpen(DbConnection connection)
    {

        lock (_gate)
        {

            ConnectionLifecycleState lifecycle = _lifecycles.GetOrCreateValue(connection);

            if (lifecycle.OpenTicket is not null)
            {

                throw new InvalidOperationException(
                    "This physical Grimoire connection already has an unresolved open attempt.");

            }

            lifecycle.OpenTicket = _admissionGate.AcquireOrdinaryOpen(connection);

            lifecycle.RefusalAfterOpenRequired = false;

        }

    }

    private bool CompleteOpen(DbConnection connection)
    {

        lock (_gate)
        {

            if (!_lifecycles.TryGetValue(connection, out ConnectionLifecycleState? lifecycle)
                || lifecycle.OpenTicket is null)
            {

                throw new InvalidOperationException(
                    "This physical Grimoire connection has no matching open ticket.");

            }

            Result admitted = lifecycle.OpenTicket.MarkOpened();

            if (admitted.IsFailure)
            {

                lifecycle.RefusalAfterOpenRequired = true;

                return false;

            }

            lifecycle.OpenTicket.Dispose();

            lifecycle.OpenTicket = null;

            if (connection is SqliteConnection sqlite && lifecycle.Enrolment is null)
            {

                lifecycle.Enrolment = _drain.Register(sqlite);

            }

            return true;

        }

    }

    private void CompleteRefusalAfterPhysicalClose(DbConnection connection)
    {

        lock (_gate)
        {

            if (!_lifecycles.TryGetValue(connection, out ConnectionLifecycleState? lifecycle)
                || lifecycle.OpenTicket is null
                || !lifecycle.RefusalAfterOpenRequired)
            {

                return;

            }

            if (connection.State != ConnectionState.Closed)
            {

                throw new InvalidOperationException(
                    "A refused Grimoire open must be physically closed before its admission ticket completes.");

            }

            lifecycle.OpenTicket.MarkRefusedAfterOpen();

            lifecycle.OpenTicket.Dispose();

            lifecycle.OpenTicket = null;

            lifecycle.RefusalAfterOpenRequired = false;

        }

    }

    private void Release(DbConnection connection)
    {

        lock (_gate)
        {

            if (!_lifecycles.TryGetValue(connection, out ConnectionLifecycleState? lifecycle))
            {

                return;

            }

            if (lifecycle.OpenTicket is not null)
            {

                if (lifecycle.RefusalAfterOpenRequired)
                {

                    if (connection.State != ConnectionState.Closed)
                    {

                        return;

                    }

                    lifecycle.OpenTicket.MarkRefusedAfterOpen();

                }
                else
                {

                    lifecycle.OpenTicket.MarkFailed();

                }

                lifecycle.OpenTicket.Dispose();

                lifecycle.OpenTicket = null;

                lifecycle.RefusalAfterOpenRequired = false;

            }

            lifecycle.Enrolment?.Dispose();

            lifecycle.Enrolment = null;

        }

    }

    private sealed class ConnectionLifecycleState
    {

        internal IGrimoireConnectionOpenTicket? OpenTicket { get; set; }

        internal IDisposable? Enrolment { get; set; }

        internal bool RefusalAfterOpenRequired { get; set; }

    }

}
