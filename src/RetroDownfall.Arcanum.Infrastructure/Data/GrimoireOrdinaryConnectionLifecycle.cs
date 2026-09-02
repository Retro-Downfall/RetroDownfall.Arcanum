using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal interface IGrimoireOrdinaryConnectionLifecycle
{

    IGrimoireOrdinaryConnectionRegistration BeginOpen(DbConnection connection);

    Result<IGrimoireOrdinaryConnectionRegistration> BorrowCurrentOpen(DbConnection connection);

    void ReleaseAfterExternalClose(DbConnection connection);

}

internal interface IGrimoireOrdinaryConnectionRegistration : IDisposable
{

    DbConnection Connection { get; }

    long Generation { get; }

    Result RevalidateAfterNativeOpen();

    Result MarkOpened();

    void MarkFailed();

    void MarkRefusedAfterOpen();

}

internal sealed class GrimoireOrdinaryConnectionLifecycle : IGrimoireOrdinaryConnectionLifecycle
{

    private readonly IGrimoireConnectionAdmissionGate _admissionGate;

    private readonly ICovenantConnectionDrain _drain;

    private readonly Lock _gate = new();

    private readonly ConditionalWeakTable<DbConnection, ConnectionLifecycleState> _lifecycles = [];

    internal GrimoireOrdinaryConnectionLifecycle(
        IGrimoireConnectionAdmissionGate admissionGate,
        ICovenantConnectionDrain drain)
    {

        ArgumentNullException.ThrowIfNull(admissionGate);

        ArgumentNullException.ThrowIfNull(drain);

        _admissionGate = admissionGate;

        _drain = drain;

    }

    public IGrimoireOrdinaryConnectionRegistration BeginOpen(DbConnection connection)
    {

        ArgumentNullException.ThrowIfNull(connection);

        lock (_gate)
        {

            if (connection.State != ConnectionState.Closed)
            {

                throw new InvalidOperationException(
                    "An ordinary Grimoire open must begin while its physical connection is closed.");

            }

            ConnectionLifecycleState lifecycle = _lifecycles.GetOrCreateValue(connection);

            if (lifecycle.OpenTicket is not null || lifecycle.HolderCount != 0)
            {

                throw new InvalidOperationException(
                    "This physical Grimoire connection already has an ordinary-open lifetime.");

            }

            IGrimoireConnectionOpenTicket ticket = _admissionGate.AcquireOrdinaryOpen(connection);

            lifecycle.OpenTicket = ticket;

            lifecycle.NativeOpenObserved = false;

            lifecycle.RefusalAfterOpenRequired = false;

            lifecycle.TicketTerminal = false;

            lifecycle.ProvenGeneration = 0;

            lifecycle.Enrolment?.Dispose();

            lifecycle.Enrolment = null;

            lifecycle.HolderCount = 1;

            lifecycle.LifetimeId++;

            return new Registration(
                this,
                lifecycle,
                connection,
                ticket.Generation,
                lifecycle.LifetimeId,
                ownsOpen: true);

        }

    }

    public Result<IGrimoireOrdinaryConnectionRegistration> BorrowCurrentOpen(DbConnection connection)
    {

        ArgumentNullException.ThrowIfNull(connection);

        lock (_gate)
        {

            if (!_lifecycles.TryGetValue(connection, out ConnectionLifecycleState? lifecycle)
                || lifecycle.HolderCount == 0
                || !lifecycle.NativeOpenObserved
                || !lifecycle.TicketTerminal
                || lifecycle.ProvenGeneration != _admissionGate.CurrentGeneration
                || lifecycle.Enrolment is null
                || connection.State != ConnectionState.Open)
            {

                return Result<IGrimoireOrdinaryConnectionRegistration>.Failure(
                    new Error(
                        ErrorCodes.Covenant.Unavailable,
                        "The physical Grimoire connection has no current admitted-open provenance."));

            }

            lifecycle.HolderCount++;

            return Result<IGrimoireOrdinaryConnectionRegistration>.Success(
                new Registration(
                    this,
                    lifecycle,
                    connection,
                    lifecycle.ProvenGeneration,
                    lifecycle.LifetimeId,
                    ownsOpen: false));

        }

    }

    public void ReleaseAfterExternalClose(DbConnection connection)
    {

        ArgumentNullException.ThrowIfNull(connection);

        lock (_gate)
        {

            if (!_lifecycles.TryGetValue(connection, out ConnectionLifecycleState? lifecycle))
            {

                return;

            }

            lifecycle.Enrolment?.Dispose();
            lifecycle.Enrolment = null;
            lifecycle.OpenTicket?.Dispose();
            lifecycle.OpenTicket = null;
            lifecycle.HolderCount = 0;
            lifecycle.NativeOpenObserved = false;
            lifecycle.RefusalAfterOpenRequired = false;
            lifecycle.TicketTerminal = false;
            lifecycle.ProvenGeneration = 0;
            lifecycle.LifetimeId++;

        }

    }

    private Result RevalidateAfterNativeOpen(Registration registration)
    {

        lock (_gate)
        {

            ConnectionLifecycleState lifecycle = RequireOpenOwner(registration);

            if (lifecycle.NativeOpenObserved)
            {

                throw new InvalidOperationException(
                    "This ordinary Grimoire open has already been revalidated after native open.");

            }

            lifecycle.NativeOpenObserved = true;

            Result revalidated = lifecycle.OpenTicket!.RevalidateAfterNativeOpen();

            lifecycle.RefusalAfterOpenRequired = revalidated.IsFailure;

            return revalidated;

        }

    }

    private Result MarkOpened(Registration registration)
    {

        lock (_gate)
        {

            ConnectionLifecycleState lifecycle = RequireOpenOwner(registration);

            if (!lifecycle.NativeOpenObserved)
            {

                throw new InvalidOperationException(
                    "An ordinary Grimoire open must be revalidated before it is marked open.");

            }

            if (registration.Connection.State != ConnectionState.Open)
            {

                throw new InvalidOperationException(
                    "An ordinary Grimoire open must remain physically open while it is admitted.");

            }

            if (registration.Connection is SqliteConnection sqlite && lifecycle.Enrolment is null)
            {

                lifecycle.Enrolment = _drain.Register(
                    sqlite,
                    () => ReleaseAfterExternalClose(sqlite));

            }

            Result opened = lifecycle.OpenTicket!.MarkOpened();

            if (opened.IsFailure)
            {

                lifecycle.RefusalAfterOpenRequired = true;

                lifecycle.Enrolment?.Dispose();

                lifecycle.Enrolment = null;

                return opened;

            }

            lifecycle.ProvenGeneration = registration.Generation;

            lifecycle.TicketTerminal = true;

            lifecycle.OpenTicket.Dispose();

            lifecycle.OpenTicket = null;

            return Result.Success();

        }

    }

    private void MarkFailed(Registration registration)
    {

        lock (_gate)
        {

            ConnectionLifecycleState lifecycle = RequireOpenOwner(registration);

            if (lifecycle.NativeOpenObserved)
            {

                throw new InvalidOperationException(
                    "A native-open attempt must be refused after open, not marked failed.");

            }

            lifecycle.OpenTicket!.MarkFailed();

            lifecycle.OpenTicket.Dispose();

            lifecycle.OpenTicket = null;

            lifecycle.TicketTerminal = true;

        }

    }

    private void MarkRefusedAfterOpen(Registration registration)
    {

        lock (_gate)
        {

            ConnectionLifecycleState lifecycle = RequireOpenOwner(registration);

            if (!lifecycle.NativeOpenObserved)
            {

                throw new InvalidOperationException(
                    "Only an observed native open can be refused after open.");

            }

            if (registration.Connection.State != ConnectionState.Closed)
            {

                throw new InvalidOperationException(
                    "A refused Grimoire open must be physically closed before its admission ticket completes.");

            }

            if (lifecycle.RefusalAfterOpenRequired)
            {

                lifecycle.OpenTicket!.MarkRefusedAfterOpen();

            }
            else
            {

                lifecycle.OpenTicket!.MarkFailed();

            }

            lifecycle.OpenTicket.Dispose();

            lifecycle.OpenTicket = null;

            lifecycle.TicketTerminal = true;

            lifecycle.NativeOpenObserved = false;

            lifecycle.RefusalAfterOpenRequired = false;

            lifecycle.ProvenGeneration = 0;

            lifecycle.Enrolment?.Dispose();

            lifecycle.Enrolment = null;

        }

    }

    private ConnectionLifecycleState RequireOpenOwner(Registration registration)
    {

        registration.ThrowIfDisposed();

        if (!registration.OwnsOpen
            || !_lifecycles.TryGetValue(
                registration.Connection,
                out ConnectionLifecycleState? lifecycle)
            || !ReferenceEquals(lifecycle, registration.Lifecycle)
            || lifecycle.OpenTicket is null)
        {

            throw new InvalidOperationException(
                "This registration does not own an unresolved ordinary Grimoire open.");

        }

        return lifecycle;

    }

    private void Release(Registration registration)
    {

        lock (_gate)
        {

            if (registration.IsDisposed)
            {

                return;

            }

            if (registration.LifetimeId != registration.Lifecycle.LifetimeId)
            {

                registration.IsDisposed = true;

                return;

            }

            if (registration.OwnsOpen && registration.Lifecycle.OpenTicket is not null)
            {

                throw new InvalidOperationException(
                    "An ordinary Grimoire open registration cannot be released before a terminal callback.");

            }

            registration.IsDisposed = true;

            ConnectionLifecycleState lifecycle = registration.Lifecycle;

            if (lifecycle.HolderCount <= 0)
            {

                throw new InvalidOperationException(
                    "The ordinary Grimoire connection holder count is invalid.");

            }

            lifecycle.HolderCount--;

            if (lifecycle.HolderCount != 0)
            {

                return;

            }

            lifecycle.Enrolment?.Dispose();

            lifecycle.Enrolment = null;

            lifecycle.NativeOpenObserved = false;

            lifecycle.RefusalAfterOpenRequired = false;

            lifecycle.TicketTerminal = false;

            lifecycle.ProvenGeneration = 0;

        }

    }

    private sealed class ConnectionLifecycleState
    {

        internal IGrimoireConnectionOpenTicket? OpenTicket { get; set; }

        internal IDisposable? Enrolment { get; set; }

        internal long ProvenGeneration { get; set; }

        internal int HolderCount { get; set; }

        internal bool NativeOpenObserved { get; set; }

        internal bool RefusalAfterOpenRequired { get; set; }

        internal bool TicketTerminal { get; set; }

        internal long LifetimeId { get; set; }

    }

    private sealed class Registration(
        GrimoireOrdinaryConnectionLifecycle owner,
        ConnectionLifecycleState lifecycle,
        DbConnection connection,
        long generation,
        long lifetimeId,
        bool ownsOpen) : IGrimoireOrdinaryConnectionRegistration
    {

        internal ConnectionLifecycleState Lifecycle { get; } = lifecycle;

        internal bool OwnsOpen { get; } = ownsOpen;

        internal bool IsDisposed { get; set; }

        public DbConnection Connection { get; } = connection;

        public long Generation { get; } = generation;

        internal long LifetimeId { get; } = lifetimeId;

        public Result RevalidateAfterNativeOpen() => owner.RevalidateAfterNativeOpen(this);

        public Result MarkOpened() => owner.MarkOpened(this);

        public void MarkFailed() => owner.MarkFailed(this);

        public void MarkRefusedAfterOpen() => owner.MarkRefusedAfterOpen(this);

        public void Dispose() => owner.Release(this);

        internal void ThrowIfDisposed()
        {

            ObjectDisposedException.ThrowIf(IsDisposed, this);

        }

    }

}
