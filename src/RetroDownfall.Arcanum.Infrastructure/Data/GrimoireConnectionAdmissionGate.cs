using System.Data.Common;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// One process-local generation state machine for every physical open of the live Grimoire.
/// </summary>
internal sealed class GrimoireConnectionAdmissionGate : IGrimoireConnectionAdmissionGate
{

    private static readonly TimeSpan ProductionOpeningAttemptTimeout = TimeSpan.FromSeconds(5);

    private const string LifecycleConflictCode = "Grimoire.AdmissionLifecycleConflict";

    private const string OpeningTimeoutCode = "Grimoire.OpeningTimeout";

    private const string StaleOpenCode = "Grimoire.StaleOpenGeneration";

    private readonly object _sync = new();

    private readonly TimeProvider _timeProvider;

    private readonly TimeSpan _openingAttemptTimeout;

    private readonly HashSet<OpenTicket> _unresolvedOpens = [];

    private GateState _state = GateState.Ordinary;

    private long _generation = 1;

    private Closure? _closure;

    private TaskCompletionSource<long> _nextOpenGeneration = NewOpenGenerationSignal();

    internal GrimoireConnectionAdmissionGate(TimeProvider timeProvider)
        : this(timeProvider, ProductionOpeningAttemptTimeout)
    {
    }

    internal GrimoireConnectionAdmissionGate(
        TimeProvider timeProvider,
        TimeSpan openingAttemptTimeout)
    {

        ArgumentNullException.ThrowIfNull(timeProvider);

        if (openingAttemptTimeout <= TimeSpan.Zero)
        {

            throw new ArgumentOutOfRangeException(nameof(openingAttemptTimeout));

        }

        _timeProvider = timeProvider;

        _openingAttemptTimeout = openingAttemptTimeout;

    }

    public long CurrentGeneration
    {

        get
        {

            lock (_sync)
            {

                return _generation;

            }

        }

    }

    public IGrimoireConnectionOpenTicket AcquireOrdinaryOpen(DbConnection connection)
    {

        ArgumentNullException.ThrowIfNull(connection);

        lock (_sync)
        {

            if (_state == GateState.Closed)
            {

                throw new GrimoireMaintenanceUnavailableException();

            }

            OpenTicket ticket = new(this, connection, _generation);

            _unresolvedOpens.Add(ticket);

            return ticket;

        }

    }

    public Result<IGrimoireClosingOwner> BeginOrResumeExclusive(
        CovenantExclusiveRecoveryOwner owner)
    {

        if (!owner.IsValid)
        {

            return Result<IGrimoireClosingOwner>.Failure(
                LifecycleConflict("An uninitialized Covenant owner cannot close Grimoire admission."));

        }

        lock (_sync)
        {

            if (_state == GateState.Ordinary)
            {

                _state = GateState.Closing;

                _closure = new Closure(owner);

            }
            else if (_closure is null || _closure.Owner != owner)
            {

                return Result<IGrimoireClosingOwner>.Failure(
                    LifecycleConflict("Another Covenant owner already controls Grimoire admission."));

            }

            if (_closure.ActiveClosedLease is not null)
            {

                return Result<IGrimoireClosingOwner>.Failure(
                    LifecycleConflict("The current Grimoire owner already holds a closed lease."));

            }

            if (_closure.ActiveClosingOwner is { IsReleased: false } current)
            {

                return Result<IGrimoireClosingOwner>.Success(current);

            }

            ClosingOwner closing = new(this, _closure, _generation);

            _closure.ActiveClosingOwner = closing;

            return Result<IGrimoireClosingOwner>.Success(closing);

        }

    }

    public async ValueTask<Result<IGrimoireExclusiveClosedLease>> CloseConnectionAdmissionAsync(
        IGrimoireClosingOwner closingOwner,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(closingOwner);

        if (closingOwner is not ClosingOwner token || !ReferenceEquals(token.Gate, this))
        {

            return Result<IGrimoireExclusiveClosedLease>.Failure(
                LifecycleConflict("The closing token does not belong to this Grimoire gate."));

        }

        Task[] terminalCallbacks;

        lock (_sync)
        {

            if (!OwnsClosingToken(token))
            {

                return Result<IGrimoireExclusiveClosedLease>.Failure(
                    LifecycleConflict("The closing token no longer owns this Grimoire transition."));

            }

            if (_state == GateState.Closing)
            {

                _generation = checked(_generation + 1);

                _state = GateState.Closed;

            }

            foreach (OpenTicket ticket in _unresolvedOpens)
            {

                ticket.RequestRefusalWhileLocked();

            }

            terminalCallbacks = _unresolvedOpens
                .Select(static ticket => ticket.TerminalCallback)
                .ToArray();

        }

        try
        {

            await Task.WhenAll(terminalCallbacks)
                .WaitAsync(_openingAttemptTimeout, _timeProvider, cancellationToken)
                .ConfigureAwait(false);

        }
        catch (TimeoutException)
        {

            return Result<IGrimoireExclusiveClosedLease>.Failure(
                new Error(
                    OpeningTimeoutCode,
                    "A physical Grimoire open did not reach its terminal callback before maintenance closing timed out."));

        }

        lock (_sync)
        {

            if (!OwnsClosingToken(token)
                || _state != GateState.Closed
                || _unresolvedOpens.Count != 0)
            {

                return Result<IGrimoireExclusiveClosedLease>.Failure(
                    LifecycleConflict("The Grimoire closing generation changed before exclusive authority was issued."));

            }

            ClosedLease lease = new(this, token.Closure, _generation);

            token.Closure.ActiveClosingOwner = null;

            token.Closure.ActiveClosedLease = lease;

            return Result<IGrimoireExclusiveClosedLease>.Success(lease);

        }

    }

    public Task<long> WaitForNextOpenGenerationAsync(
        long observedGeneration,
        CancellationToken cancellationToken)
    {

        if (observedGeneration < 0)
        {

            throw new ArgumentOutOfRangeException(nameof(observedGeneration));

        }

        lock (_sync)
        {

            if (_state == GateState.Ordinary && _generation > observedGeneration)
            {

                return Task.FromResult(_generation);

            }

            return _nextOpenGeneration.Task.WaitAsync(cancellationToken);

        }

    }

    private static TaskCompletionSource<long> NewOpenGenerationSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Error LifecycleConflict(string message) =>
        new(LifecycleConflictCode, message);

    private bool OwnsClosingToken(ClosingOwner token) =>
        !token.IsReleased
        && ReferenceEquals(_closure, token.Closure)
        && ReferenceEquals(token.Closure.ActiveClosingOwner, token)
        && token.Closure.Owner == token.Owner
        && token.Generation <= _generation
        && token.Closure.ActiveClosedLease is null;

    private Result MarkOpened(OpenTicket ticket)
    {

        lock (_sync)
        {

            ticket.RequireStateWhileLocked(OpenTicketState.Opening);

            if (ticket.RefusalRequested
                || ticket.Generation != _generation
                || _state == GateState.Closed)
            {

                ticket.State = OpenTicketState.RefusedAfterOpenRequired;

                return Result.Failure(
                    new Error(
                        StaleOpenCode,
                        "The physical Grimoire open lost its admission generation and must be closed."));

            }

            CompleteTicketWhileLocked(ticket);

            return Result.Success();

        }

    }

    private void MarkFailed(OpenTicket ticket)
    {

        lock (_sync)
        {

            if (ticket.State != OpenTicketState.Opening)
            {

                throw new InvalidOperationException(
                    "This Grimoire open ticket already used its terminal transition.");

            }

            CompleteTicketWhileLocked(ticket);

        }

    }

    private void MarkRefusedAfterOpen(OpenTicket ticket)
    {

        lock (_sync)
        {

            ticket.RequireStateWhileLocked(OpenTicketState.RefusedAfterOpenRequired);

            CompleteTicketWhileLocked(ticket);

        }

    }

    private void DisposeTicket(OpenTicket ticket)
    {

        lock (_sync)
        {

            if (ticket.State == OpenTicketState.Opening)
            {

                CompleteTicketWhileLocked(ticket);

            }

        }

    }

    private void CompleteTicketWhileLocked(OpenTicket ticket)
    {

        ticket.State = OpenTicketState.Terminal;

        _ = _unresolvedOpens.Remove(ticket);

        ticket.SignalTerminalWhileLocked();

    }

    private void ReleaseClosingOwner(ClosingOwner token)
    {

        lock (_sync)
        {

            if (ReferenceEquals(token.Closure.ActiveClosingOwner, token))
            {

                token.Closure.ActiveClosingOwner = null;

            }

        }

    }

    private Result CompleteClosedLease(
        ClosedLease lease,
        CovenantExclusiveLeaseDisposition disposition)
    {

        TaskCompletionSource<long>? opened = null;

        long openGeneration = 0;

        lock (_sync)
        {

            if (_state != GateState.Closed
                || !ReferenceEquals(_closure, lease.Closure)
                || !ReferenceEquals(lease.Closure.ActiveClosedLease, lease)
                || lease.Closure.Owner != lease.Owner
                || lease.Generation != _generation)
            {

                return LifecycleConflict(
                    "The exclusive Grimoire lease no longer owns its exact closed generation.");

            }

            lease.Closure.ActiveClosedLease = null;

            if (disposition != CovenantExclusiveLeaseDisposition.KeepClosed)
            {

                _state = GateState.Ordinary;

                _closure = null;

                openGeneration = _generation;

                opened = _nextOpenGeneration;

                _nextOpenGeneration = NewOpenGenerationSignal();

            }

        }

        _ = opened?.TrySetResult(openGeneration);

        return Result.Success();

    }

    private void ReleaseClosedLease(ClosedLease lease)
    {

        lock (_sync)
        {

            if (ReferenceEquals(lease.Closure.ActiveClosedLease, lease))
            {

                lease.Closure.ActiveClosedLease = null;

            }

        }

    }

    private enum GateState : byte
    {

        Ordinary = 1,

        Closing = 2,

        Closed = 3,

    }

    private enum OpenTicketState : byte
    {

        Opening = 1,

        RefusedAfterOpenRequired = 2,

        Terminal = 3,

    }

    private sealed class Closure(CovenantExclusiveRecoveryOwner owner)
    {

        internal CovenantExclusiveRecoveryOwner Owner { get; } = owner;

        internal ClosingOwner? ActiveClosingOwner { get; set; }

        internal ClosedLease? ActiveClosedLease { get; set; }

    }

    private sealed class OpenTicket(
        GrimoireConnectionAdmissionGate gate,
        DbConnection connection,
        long generation) : IGrimoireConnectionOpenTicket
    {

        private readonly TaskCompletionSource _terminal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _disposed;

        internal DbConnection Connection { get; } = connection;

        public long Generation { get; } = generation;

        internal OpenTicketState State { get; set; } = OpenTicketState.Opening;

        internal bool RefusalRequested { get; private set; }

        internal Task TerminalCallback => _terminal.Task;

        public Result MarkOpened()
        {

            ThrowIfDisposed();

            return gate.MarkOpened(this);

        }

        public void MarkFailed()
        {

            ThrowIfDisposed();

            gate.MarkFailed(this);

        }

        public void MarkRefusedAfterOpen()
        {

            ThrowIfDisposed();

            gate.MarkRefusedAfterOpen(this);

        }

        public void Dispose()
        {

            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {

                return;

            }

            gate.DisposeTicket(this);

            GC.SuppressFinalize(this);

        }

        internal void RequestRefusalWhileLocked()
        {

            if (State == OpenTicketState.Opening)
            {

                RefusalRequested = true;

            }

        }

        internal void RequireStateWhileLocked(OpenTicketState required)
        {

            if (State != required)
            {

                throw new InvalidOperationException(
                    "This Grimoire open ticket cannot use that transition from its current state.");

            }

        }

        internal void SignalTerminalWhileLocked() => _terminal.TrySetResult();

        private void ThrowIfDisposed() =>
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    }

    private sealed class ClosingOwner(
        GrimoireConnectionAdmissionGate gate,
        Closure closure,
        long generation) : IGrimoireClosingOwner
    {

        private int _released;

        internal GrimoireConnectionAdmissionGate Gate { get; } = gate;

        internal Closure Closure { get; } = closure;

        internal bool IsReleased => Volatile.Read(ref _released) != 0;

        public CovenantExclusiveRecoveryOwner Owner => Closure.Owner;

        public long Generation { get; } = generation;

        public ValueTask DisposeAsync()
        {

            if (Interlocked.Exchange(ref _released, 1) == 0)
            {

                Gate.ReleaseClosingOwner(this);

            }

            GC.SuppressFinalize(this);

            return ValueTask.CompletedTask;

        }

    }

    private sealed class ClosedLease(
        GrimoireConnectionAdmissionGate gate,
        Closure closure,
        long generation) : IGrimoireExclusiveClosedLease
    {

        private int _dispositionClaimed;

        private int _released;

        internal Closure Closure { get; } = closure;

        public CovenantExclusiveRecoveryOwner Owner => Closure.Owner;

        public long Generation { get; } = generation;

        public ValueTask<Result> CompleteAsync(
            CovenantExclusiveLeaseDisposition disposition,
            CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            if (disposition is not CovenantExclusiveLeaseDisposition.RollbackAndReopen
                and not CovenantExclusiveLeaseDisposition.CommitAndReopen
                and not CovenantExclusiveLeaseDisposition.KeepClosed)
            {

                throw new ArgumentOutOfRangeException(nameof(disposition));

            }

            if (Volatile.Read(ref _released) != 0)
            {

                return ValueTask.FromResult(
                    Result.Failure(
                        LifecycleConflict("This exclusive Grimoire lease has already been released.")));

            }

            if (Interlocked.Exchange(ref _dispositionClaimed, 1) != 0)
            {

                return ValueTask.FromResult(
                    Result.Failure(
                        LifecycleConflict("This exclusive Grimoire lease already used its disposition.")));

            }

            return ValueTask.FromResult(gate.CompleteClosedLease(this, disposition));

        }

        public ValueTask DisposeAsync()
        {

            if (Interlocked.Exchange(ref _released, 1) == 0)
            {

                gate.ReleaseClosedLease(this);

            }

            GC.SuppressFinalize(this);

            return ValueTask.CompletedTask;

        }

    }

}
