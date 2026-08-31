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

    private const string WorkDrainTimeoutCode = "Grimoire.WorkDrainTimeout";

    private const string StaleOpenCode = "Grimoire.StaleOpenGeneration";

    private readonly object _sync = new();

    private static readonly AsyncLocal<OrdinaryLifetime?> CurrentOrdinaryLifetime = new();

    private readonly TimeProvider _timeProvider;

    private readonly TimeSpan _openingAttemptTimeout;

    private readonly HashSet<OpenTicket> _unresolvedOpens = [];

    private readonly HashSet<RequestLease> _requestLeases = [];

    private readonly HashSet<WorkLease> _workLeases = [];

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

    public bool TryAcquireRequestLease(
        GrimoireRequestKind kind,
        out IGrimoireRequestLease? lease)
    {

        if (kind is not GrimoireRequestKind.Finite
            and not GrimoireRequestKind.QuiesceableStream)
        {

            throw new ArgumentOutOfRangeException(nameof(kind));

        }

        lock (_sync)
        {

            if (_state != GateState.Ordinary)
            {

                lease = null;

                return false;

            }

            RequestLease admitted = new(
                this,
                kind,
                _generation,
                CurrentOrdinaryLifetime.Value);

            _requestLeases.Add(admitted);

            CurrentOrdinaryLifetime.Value = admitted.Lifetime;

            lease = admitted;

            return true;

        }

    }

    public bool TryAcquireWorkLease(
        GrimoireWorkKind kind,
        out IGrimoireWorkLease? lease)
    {

        if (kind is not GrimoireWorkKind.SessionAttachmentIndexing
            and not GrimoireWorkKind.EntryWeaving
            and not GrimoireWorkKind.SagaExtraction)
        {

            throw new ArgumentOutOfRangeException(nameof(kind));

        }

        lock (_sync)
        {

            if (_state != GateState.Ordinary)
            {

                lease = null;

                return false;

            }

            WorkLease admitted = new(
                this,
                kind,
                _generation,
                CurrentOrdinaryLifetime.Value);

            _workLeases.Add(admitted);

            CurrentOrdinaryLifetime.Value = admitted.Lifetime;

            lease = admitted;

            return true;

        }

    }

    public IGrimoireConnectionOpenTicket AcquireOrdinaryOpen(DbConnection connection)
    {

        ArgumentNullException.ThrowIfNull(connection);

        lock (_sync)
        {

            if (_state == GateState.Closed
                || (_state == GateState.Closing && !HasLiveFinisherLifetimeWhileLocked()))
            {

                throw new GrimoireMaintenanceUnavailableException();

            }

            OpenTicket ticket = new(this, connection, _generation);

            _unresolvedOpens.Add(ticket);

            return ticket;

        }

    }

    public Result<IGrimoireClosingOwner> BeginOrResumeExclusive(
        CovenantExclusiveRecoveryOwner owner,
        IGrimoireRequestLease? initiatingRequest = null,
        DbConnection? scopedConnection = null)
    {

        if (!owner.IsValid)
        {

            return Result<IGrimoireClosingOwner>.Failure(
                LifecycleConflict("An uninitialized Covenant owner cannot close Grimoire admission."));

        }

        if ((initiatingRequest is null) != (scopedConnection is null))
        {

            return Result<IGrimoireClosingOwner>.Failure(
                LifecycleConflict(
                    "Initiator promotion requires both the exact request lease and scoped connection."));

        }

        List<CancellationTokenSource>? revocations = null;

        Result<IGrimoireClosingOwner> result;

        lock (_sync)
        {

            if (_state == GateState.Ordinary)
            {

                RequestLease? promoted = null;

                if (initiatingRequest is not null)
                {

                    if (initiatingRequest is not RequestLease request
                        || !ReferenceEquals(request.Gate, this)
                        || request.IsReleased
                        || request.IsPromoted
                        || !_requestLeases.Contains(request))
                    {

                        return Result<IGrimoireClosingOwner>.Failure(
                            LifecycleConflict(
                                "The initiating request is not the exact live request lease owned by this gate."));

                    }

                    promoted = request;

                }

                _state = GateState.Closing;

                _closure = new Closure(owner, promoted, scopedConnection);

                if (promoted is not null)
                {

                    promoted.IsPromoted = true;

                    _ = _requestLeases.Remove(promoted);

                    promoted.SignalTerminalWhileLocked();

                }

                _closure.StageOneDrained =
                    _requestLeases.Count == 0 && _workLeases.Count == 0;

                revocations = [];

                revocations.AddRange(
                    _requestLeases
                        .Where(static request =>
                            request.Kind == GrimoireRequestKind.QuiesceableStream)
                        .Select(static request => request.Revocation));

                revocations.AddRange(
                    _workLeases.Select(static work => work.Revocation));

            }
            else if (_closure is null || _closure.Owner != owner)
            {

                return Result<IGrimoireClosingOwner>.Failure(
                    LifecycleConflict("Another Covenant owner already controls Grimoire admission."));

            }

            else if (initiatingRequest is not null
                && (!ReferenceEquals(_closure.InitiatingRequest, initiatingRequest)
                    || !ReferenceEquals(_closure.ScopedConnection, scopedConnection)))
            {

                return Result<IGrimoireClosingOwner>.Failure(
                    LifecycleConflict(
                        "The resumed Grimoire transition does not match its exact initiating request and connection."));

            }

            if (_closure.ActiveClosedLease is not null)
            {

                return Result<IGrimoireClosingOwner>.Failure(
                    LifecycleConflict("The current Grimoire owner already holds a closed lease."));

            }

            if (_closure.ActiveClosingOwner is { IsReleased: false } current)
            {

                result = Result<IGrimoireClosingOwner>.Success(current);

            }
            else
            {

                ClosingOwner closing = new(this, _closure, _generation);

                _closure.ActiveClosingOwner = closing;

                result = Result<IGrimoireClosingOwner>.Success(closing);

            }

        }

        if (revocations is not null)
        {

            foreach (CancellationTokenSource revocation in revocations)
            {

                try
                {

                    revocation.Cancel();

                }
                catch (AggregateException)
                {

                    // A consumer callback cannot take ownership of the already-linearized
                    // maintenance transition or prevent later lifetimes from being signalled.

                }

            }

        }

        return result;

    }

    public async ValueTask<Result> DrainRequestAndWorkAsync(
        IGrimoireClosingOwner closingOwner,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(closingOwner);

        if (closingOwner is not ClosingOwner token || !ReferenceEquals(token.Gate, this))
        {

            return LifecycleConflict(
                "The closing token does not belong to this Grimoire gate.");

        }

        Task[] terminalLifetimes;

        lock (_sync)
        {

            if (!OwnsClosingToken(token) || _state != GateState.Closing)
            {

                return LifecycleConflict(
                    "The closing token no longer owns the request and work drain.");

            }

            if (_requestLeases.Count == 0 && _workLeases.Count == 0)
            {

                token.Closure.StageOneDrained = true;

                return Result.Success();

            }

            terminalLifetimes = _requestLeases
                .Select(static request => request.Terminal)
                .Concat(_workLeases.Select(static work => work.Terminal))
                .ToArray();

        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {

            await Task.WhenAll(terminalLifetimes)
                .WaitAsync(_openingAttemptTimeout, _timeProvider, cancellationToken)
                .ConfigureAwait(false);

        }
        catch (TimeoutException)
        {

            lock (_sync)
            {

                if (OwnsClosingToken(token) && _state == GateState.Closing)
                {

                    token.Closure.StageOneTimedOut = true;

                }

            }

            return Result.Failure(
                new Error(
                    WorkDrainTimeoutCode,
                    "Ordinary Grimoire request or background work did not drain before maintenance closing timed out."));

        }

        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {

            if (!OwnsClosingToken(token)
                || _state != GateState.Closing
                || _requestLeases.Count != 0
                || _workLeases.Count != 0)
            {

                return LifecycleConflict(
                    "The Grimoire closing generation changed before request and work drain completed.");

            }

            token.Closure.StageOneDrained = true;

            return Result.Success();

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

            if (!token.Closure.StageOneDrained
                || _requestLeases.Count != 0
                || _workLeases.Count != 0)
            {

                return Result<IGrimoireExclusiveClosedLease>.Failure(
                    LifecycleConflict(
                        "Ordinary Grimoire request and background work must drain before connection admission closes."));

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

        cancellationToken.ThrowIfCancellationRequested();

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

        cancellationToken.ThrowIfCancellationRequested();

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

    public async ValueTask<Result> AbortClosingAsync(
        IGrimoireClosingOwner closingOwner,
        Func<CancellationToken, ValueTask<bool>> proveNoDestructiveEffectAsync,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(closingOwner);

        ArgumentNullException.ThrowIfNull(proveNoDestructiveEffectAsync);

        if (closingOwner is not ClosingOwner token || !ReferenceEquals(token.Gate, this))
        {

            return LifecycleConflict(
                "The closing token does not belong to this Grimoire gate.");

        }

        lock (_sync)
        {

            if (!OwnsClosingToken(token)
                || _state != GateState.Closing
                || !token.Closure.StageOneTimedOut)
            {

                return LifecycleConflict(
                    "Only the exact owner of a timed-out stage-one transition may request abort.");

            }

        }

        cancellationToken.ThrowIfCancellationRequested();

        bool provenSafe = await proveNoDestructiveEffectAsync(cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        if (!provenSafe)
        {

            return LifecycleConflict(
                "The Grimoire transition cannot abort without proof that no destructive effect occurred.");

        }

        TaskCompletionSource<long> opened;

        long openGeneration;

        lock (_sync)
        {

            if (!OwnsClosingToken(token)
                || _state != GateState.Closing
                || !token.Closure.StageOneTimedOut)
            {

                return LifecycleConflict(
                    "The Grimoire closing generation changed before the proven abort completed.");

            }

            token.Closure.ActiveClosingOwner = null;

            _state = GateState.Ordinary;

            _closure = null;

            _generation = checked(_generation + 1);

            openGeneration = _generation;

            opened = _nextOpenGeneration;

            _nextOpenGeneration = NewOpenGenerationSignal();

        }

        _ = opened.TrySetResult(openGeneration);

        return Result.Success();

    }

    public Task<long> WaitForNextOpenGenerationAsync(
        long observedGeneration,
        CancellationToken cancellationToken)
    {

        if (observedGeneration < 0)
        {

            throw new ArgumentOutOfRangeException(nameof(observedGeneration));

        }

        if (cancellationToken.IsCancellationRequested)
        {

            return Task.FromCanceled<long>(cancellationToken);

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

    private bool HasLiveFinisherLifetimeWhileLocked()
    {

        for (OrdinaryLifetime? lifetime = CurrentOrdinaryLifetime.Value;
            lifetime is not null;
            lifetime = lifetime.Previous)
        {

            if (ReferenceEquals(lifetime.Gate, this)
                && !lifetime.IsReleased
                && lifetime.Generation <= _generation)
            {

                return true;

            }

        }

        return false;

    }

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

    private void ReleaseRequest(RequestLease lease)
    {

        lock (_sync)
        {

            if (!lease.IsPromoted)
            {

                _ = _requestLeases.Remove(lease);

            }

            lease.SignalTerminalWhileLocked();

        }

    }

    private bool TryBeginExternalEffectGroup(
        WorkLease lease,
        out IGrimoireExternalEffectGroup? effectGroup)
    {

        lock (_sync)
        {

            if (_state != GateState.Ordinary
                || lease.IsReleased
                || !_workLeases.Contains(lease)
                || lease.Generation != _generation
                || lease.MaintenanceRevocation.IsCancellationRequested
                || lease.ActiveEffectGroup is not null)
            {

                effectGroup = null;

                return false;

            }

            ExternalEffectGroup admitted = new(this, lease);

            lease.ActiveEffectGroup = admitted;

            effectGroup = admitted;

            return true;

        }

    }

    private void ReleaseWorkScope(WorkLease lease)
    {

        lock (_sync)
        {

            lease.ScopeDisposed = true;

            CompleteWorkLeaseIfDrainedWhileLocked(lease);

        }

    }

    private void ReleaseExternalEffectGroup(ExternalEffectGroup effectGroup)
    {

        lock (_sync)
        {

            WorkLease lease = effectGroup.Lease;

            if (ReferenceEquals(lease.ActiveEffectGroup, effectGroup))
            {

                lease.ActiveEffectGroup = null;

            }

            CompleteWorkLeaseIfDrainedWhileLocked(lease);

        }

    }

    private void CompleteWorkLeaseIfDrainedWhileLocked(WorkLease lease)
    {

        if (!lease.ScopeDisposed || lease.ActiveEffectGroup is not null)
        {

            return;

        }

        _ = _workLeases.Remove(lease);

        lease.SignalTerminalWhileLocked();

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

    private sealed class Closure(
        CovenantExclusiveRecoveryOwner owner,
        RequestLease? initiatingRequest,
        DbConnection? scopedConnection)
    {

        internal CovenantExclusiveRecoveryOwner Owner { get; } = owner;

        internal RequestLease? InitiatingRequest { get; } = initiatingRequest;

        internal DbConnection? ScopedConnection { get; } = scopedConnection;

        internal ClosingOwner? ActiveClosingOwner { get; set; }

        internal ClosedLease? ActiveClosedLease { get; set; }

        internal bool StageOneDrained { get; set; }

        internal bool StageOneTimedOut { get; set; }

    }

    private sealed class OrdinaryLifetime(
        GrimoireConnectionAdmissionGate gate,
        long generation,
        OrdinaryLifetime? previous)
    {

        internal GrimoireConnectionAdmissionGate Gate { get; } = gate;

        internal long Generation { get; } = generation;

        internal OrdinaryLifetime? Previous { get; } = previous;

        internal bool IsReleased { get; set; }

    }

    private sealed class RequestLease : IGrimoireRequestLease
    {

        private readonly TaskCompletionSource _terminal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _released;

        internal RequestLease(
            GrimoireConnectionAdmissionGate gate,
            GrimoireRequestKind kind,
            long generation,
            OrdinaryLifetime? previousLifetime)
        {

            Gate = gate;

            Kind = kind;

            Generation = generation;

            Lifetime = new OrdinaryLifetime(gate, generation, previousLifetime);

        }

        internal GrimoireConnectionAdmissionGate Gate { get; }

        internal OrdinaryLifetime Lifetime { get; }

        internal CancellationTokenSource Revocation { get; } = new();

        internal bool IsReleased => Volatile.Read(ref _released) != 0;

        internal bool IsPromoted { get; set; }

        internal Task Terminal => _terminal.Task;

        public GrimoireRequestKind Kind { get; }

        public long Generation { get; }

        public CancellationToken MaintenanceRevocation => Revocation.Token;

        public ValueTask DisposeAsync()
        {

            if (Interlocked.Exchange(ref _released, 1) == 0)
            {

                Lifetime.IsReleased = true;

                if (ReferenceEquals(CurrentOrdinaryLifetime.Value, Lifetime))
                {

                    CurrentOrdinaryLifetime.Value = Lifetime.Previous;

                }

                Gate.ReleaseRequest(this);

            }

            GC.SuppressFinalize(this);

            return ValueTask.CompletedTask;

        }

        internal void SignalTerminalWhileLocked() => _terminal.TrySetResult();

    }

    private sealed class WorkLease : IGrimoireWorkLease
    {

        private readonly TaskCompletionSource _terminal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _released;

        internal WorkLease(
            GrimoireConnectionAdmissionGate gate,
            GrimoireWorkKind kind,
            long generation,
            OrdinaryLifetime? previousLifetime)
        {

            Gate = gate;

            Kind = kind;

            Generation = generation;

            Lifetime = new OrdinaryLifetime(gate, generation, previousLifetime);

        }

        internal GrimoireConnectionAdmissionGate Gate { get; }

        internal OrdinaryLifetime Lifetime { get; }

        internal CancellationTokenSource Revocation { get; } = new();

        internal bool IsReleased => Volatile.Read(ref _released) != 0;

        internal bool ScopeDisposed { get; set; }

        internal ExternalEffectGroup? ActiveEffectGroup { get; set; }

        internal Task Terminal => _terminal.Task;

        public GrimoireWorkKind Kind { get; }

        public long Generation { get; }

        public CancellationToken MaintenanceRevocation => Revocation.Token;

        public bool TryBeginExternalEffectGroup(
            out IGrimoireExternalEffectGroup? effectGroup) =>
            Gate.TryBeginExternalEffectGroup(this, out effectGroup);

        public ValueTask DisposeAsync()
        {

            if (Interlocked.Exchange(ref _released, 1) == 0)
            {

                Lifetime.IsReleased = true;

                if (ReferenceEquals(CurrentOrdinaryLifetime.Value, Lifetime))
                {

                    CurrentOrdinaryLifetime.Value = Lifetime.Previous;

                }

                Gate.ReleaseWorkScope(this);

            }

            GC.SuppressFinalize(this);

            return ValueTask.CompletedTask;

        }

        internal void SignalTerminalWhileLocked() => _terminal.TrySetResult();

    }

    private sealed class ExternalEffectGroup(
        GrimoireConnectionAdmissionGate gate,
        WorkLease lease) : IGrimoireExternalEffectGroup
    {

        private int _released;

        internal WorkLease Lease { get; } = lease;

        public ValueTask DisposeAsync()
        {

            if (Interlocked.Exchange(ref _released, 1) == 0)
            {

                gate.ReleaseExternalEffectGroup(this);

            }

            GC.SuppressFinalize(this);

            return ValueTask.CompletedTask;

        }

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
