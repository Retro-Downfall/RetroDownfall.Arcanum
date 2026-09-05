using System.Data.Common;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// One process-local generation state machine for every physical open of the live Grimoire.
/// </summary>
internal sealed class GrimoireConnectionAdmissionGate : IGrimoireConnectionAdmissionGate
{

    private static readonly TimeSpan ProductionOpeningAttemptTimeout = TimeSpan.FromSeconds(5);

    private const string LifecycleConflictCode = "Grimoire.AdmissionLifecycleConflict";

    private const string OpeningTimeoutCode = "Grimoire.OpeningTimeout";

    /// <summary>
    /// The stage-one drain's own refusal, which is the one drain failure an owner may abort out of.
    /// </summary>
    internal const string WorkDrainTimeoutCode = ErrorCodes.Grimoire.WorkDrainTimeout;

    private const string StaleOpenCode = "Grimoire.StaleOpenGeneration";

    private readonly object _sync = new();

    private static readonly AsyncLocal<OrdinaryLifetime?> CurrentOrdinaryLifetime = new();

    private readonly TimeProvider _timeProvider;

    private readonly ICovenantConnectionDrain _drain;

    private readonly TimeSpan _openingAttemptTimeout;

    /// <summary>
    /// How long stage one waits for ordinary requests and background work to finish.
    /// </summary>
    /// <remarks>
    /// A separate deadline from <see cref="_openingAttemptTimeout"/> because it bounds a different
    /// thing - session-attachment indexing, entry weaving and saga extraction rather than one
    /// physical connection reaching its terminal callback - and its expiry is reported under its own
    /// error code. Both default to the same value, so separating them moves no deadline; it gives
    /// the failure an operator reads a knob of its own to turn.
    /// </remarks>
    private readonly TimeSpan _workDrainCheckpoint;

    private readonly IGrimoireMaintenancePathAuthority _paths;

    private readonly Func<CancellationToken, ValueTask> _afterSuccessfulDrainTestSeam;

    private readonly HashSet<OpenTicket> _unresolvedOpens = [];

    private readonly HashSet<RequestLease> _requestLeases = [];

    private readonly HashSet<WorkLease> _workLeases = [];

    private readonly SemaphoreSlim _maintenanceAdoptionInterlock = new(1, 1);

    private GateState _state = GateState.Ordinary;

    private long _generation = 1;

    private Closure? _closure;

    private object? _maintenanceAdoptionInterlockOwner;

    private TaskCompletionSource<long> _nextOpenGeneration = NewOpenGenerationSignal();

    private long _materializedTerminalCallbacks;

    internal GrimoireConnectionAdmissionGate(TimeProvider timeProvider)
        : this(
            timeProvider,
            new CovenantConnectionDrain(),
            ProductionOpeningAttemptTimeout,
            AfterSuccessfulDrainNoOpAsync)
    {
    }

    /// <summary>The composed gate, told where its maintenance purposes point.</summary>
    internal GrimoireConnectionAdmissionGate(
        TimeProvider timeProvider,
        ICovenantConnectionDrain drain,
        IGrimoireMaintenancePathAuthority paths)
        : this(
            timeProvider,
            drain,
            ProductionOpeningAttemptTimeout,
            ProductionOpeningAttemptTimeout,
            AfterSuccessfulDrainNoOpAsync,
            paths)
    {
    }

    internal GrimoireConnectionAdmissionGate(
        TimeProvider timeProvider,
        ICovenantConnectionDrain drain)
        : this(
            timeProvider,
            drain,
            ProductionOpeningAttemptTimeout,
            AfterSuccessfulDrainNoOpAsync)
    {
    }

    internal GrimoireConnectionAdmissionGate(
        TimeProvider timeProvider,
        TimeSpan openingAttemptTimeout)
        : this(
            timeProvider,
            new CovenantConnectionDrain(),
            openingAttemptTimeout,
            AfterSuccessfulDrainNoOpAsync)
    {
    }

    internal GrimoireConnectionAdmissionGate(
        TimeProvider timeProvider,
        ICovenantConnectionDrain drain,
        TimeSpan openingAttemptTimeout)
        : this(
            timeProvider,
            drain,
            openingAttemptTimeout,
            AfterSuccessfulDrainNoOpAsync)
    {
    }

    internal GrimoireConnectionAdmissionGate(
        TimeProvider timeProvider,
        ICovenantConnectionDrain drain,
        TimeSpan openingAttemptTimeout,
        TimeSpan workDrainCheckpoint)
        : this(
            timeProvider,
            drain,
            openingAttemptTimeout,
            workDrainCheckpoint,
            AfterSuccessfulDrainNoOpAsync,
            GrimoireInstallationMaintenancePaths.Instance)
    {
    }

    internal GrimoireConnectionAdmissionGate(
        TimeProvider timeProvider,
        ICovenantConnectionDrain drain,
        TimeSpan openingAttemptTimeout,
        Func<CancellationToken, ValueTask> afterSuccessfulDrainTestSeam)
        : this(
            timeProvider,
            drain,
            openingAttemptTimeout,
            openingAttemptTimeout,
            afterSuccessfulDrainTestSeam,
            GrimoireInstallationMaintenancePaths.Instance)
    {
    }

    internal GrimoireConnectionAdmissionGate(
        TimeProvider timeProvider,
        ICovenantConnectionDrain drain,
        TimeSpan openingAttemptTimeout,
        TimeSpan workDrainCheckpoint,
        Func<CancellationToken, ValueTask> afterSuccessfulDrainTestSeam,
        IGrimoireMaintenancePathAuthority paths)
    {

        ArgumentNullException.ThrowIfNull(timeProvider);

        ArgumentNullException.ThrowIfNull(drain);

        ArgumentNullException.ThrowIfNull(afterSuccessfulDrainTestSeam);

        ArgumentNullException.ThrowIfNull(paths);

        if (openingAttemptTimeout <= TimeSpan.Zero)
        {

            throw new ArgumentOutOfRangeException(nameof(openingAttemptTimeout));

        }

        if (workDrainCheckpoint <= TimeSpan.Zero)
        {

            throw new ArgumentOutOfRangeException(nameof(workDrainCheckpoint));

        }

        _timeProvider = timeProvider;

        _drain = drain;

        _openingAttemptTimeout = openingAttemptTimeout;

        _workDrainCheckpoint = workDrainCheckpoint;

        _paths = paths;

        _afterSuccessfulDrainTestSeam = afterSuccessfulDrainTestSeam;

    }

    /// <summary>
    /// How many open tickets have had terminal-callback machinery built for them.
    /// </summary>
    /// <remarks>
    /// Every physical Grimoire open takes a ticket, and under a pooled context that is every EF
    /// operation. The callback has exactly one reader - a stage-two close waiting for opens already
    /// in flight - so on a gate that has never been closed it is machinery built for nobody.
    /// Counted rather than inferred, because an allocation per open is invisible to every other
    /// assertion the suite can make.
    /// </remarks>
    internal long MaterializedTerminalCallbacks => Interlocked.Read(ref _materializedTerminalCallbacks);

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
                || (_state == GateState.Closing
                    && !HasLiveFinisherLifetimeWhileLocked(connection)))
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

                    promoted.Lifetime.PromotedConnection = scopedConnection;

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
                    _workLeases
                        .Where(static work => work.ActiveEffectGroup is null)
                        .Select(static work => work.Revocation));

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
                .WaitAsync(_workDrainCheckpoint, _timeProvider, cancellationToken)
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

        Closure closure;

        long closedGeneration;

        // The lock block below commits stage two in one step - the generation bump, the move to
        // Closed, and the refusal stamped on every unresolved open. Reporting cancellation after
        // that commitment tells the caller nothing happened while the gate is permanently Closed on
        // a burned generation, so the token is honoured here, before the transition, where refusing
        // is a pure no-op that leaves the closing owner and its abort path intact.
        cancellationToken.ThrowIfCancellationRequested();

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

            if (token.Closure.StageTwoInProgress)
            {

                return Result<IGrimoireExclusiveClosedLease>.Failure(
                    LifecycleConflict(
                        "The exact Grimoire owner already has a stage-two close in progress."));

            }

            if (_state == GateState.Closing)
            {

                _generation = checked(_generation + 1);

                _state = GateState.Closed;

            }

            token.Closure.StageTwoInProgress = true;

            closure = token.Closure;

            closedGeneration = _generation;

            foreach (OpenTicket ticket in _unresolvedOpens)
            {

                ticket.RequestRefusalWhileLocked();

            }

            terminalCallbacks = _unresolvedOpens
                .Select(static ticket => ticket.TerminalCallbackWhileLocked())
                .ToArray();

        }

        try
        {

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

            Result drained = await _drain.DrainAsync(cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (drained.IsFailure)
            {

                return Result<IGrimoireExclusiveClosedLease>.Failure(drained.Error);

            }

            await _afterSuccessfulDrainTestSeam(cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            lock (_sync)
            {

                if (!OwnsClosingToken(token)
                    || !ReferenceEquals(_closure, closure)
                    || _state != GateState.Closed
                    || _generation != closedGeneration
                    || !closure.StageOneDrained
                    || !closure.StageTwoInProgress
                    || _unresolvedOpens.Count != 0
                    || _maintenanceAdoptionInterlockOwner is not null
                    || closure.ActiveScopedConnectionPermit is { IsReleased: false }
                    || closure.LiveOneShotAuthorities.Count != 0
                    || closure.LiveMaintenanceHandles.Count != 0)
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
        finally
        {

            ResetStageTwoAttempt(closure);

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

        // Abort is the only way out of a stage one that timed out, and while the gate is Closing
        // every request and work lease is refused. The caller most likely to reach here is a host
        // unwinding under an ambient token that has already fired, so honouring that token would
        // withhold the escape hatch from exactly the caller who needs it and leave the gate Closing
        // with nothing left to release it. The proof runs uncancelled for the same reason: a proof
        // abandoned halfway is indistinguishable from a proof that failed.
        _ = cancellationToken;

        bool provenSafe = await proveNoDestructiveEffectAsync(CancellationToken.None)
            .ConfigureAwait(false);

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

    public async ValueTask<Result<IGrimoireExpiredLeaseAdoptionInterlock>>
        AcquireExpiredLeaseAdoptionInterlockAsync(
            CovenantExclusiveRecoveryOwner candidateOwner,
            Func<CovenantExclusiveRecoveryOwner, CancellationToken, ValueTask<bool>>
                revalidateDurableOwnerAsync,
            CancellationToken cancellationToken)
    {

        if (!candidateOwner.IsValid)
        {

            return Result<IGrimoireExpiredLeaseAdoptionInterlock>.Failure(
                LifecycleConflict(
                    "An uninitialized Covenant owner cannot acquire the expired-owner adoption interlock."));

        }

        ArgumentNullException.ThrowIfNull(revalidateDurableOwnerAsync);

        cancellationToken.ThrowIfCancellationRequested();

        await _maintenanceAdoptionInterlock
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        bool releaseInterlock = true;

        try
        {

            cancellationToken.ThrowIfCancellationRequested();

            bool stillAdoptable = await revalidateDurableOwnerAsync(
                    candidateOwner,
                    cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (!stillAdoptable)
            {

                return Result<IGrimoireExpiredLeaseAdoptionInterlock>.Failure(
                    LifecycleConflict(
                        "The durable Covenant owner was no longer adoptable after the adoption interlock was acquired."));

            }

            ExpiredLeaseAdoptionInterlock interlock = new(this, candidateOwner);

            lock (_sync)
            {

                if (_maintenanceAdoptionInterlockOwner is not null
                    || IsClosedLeaseReservationPendingWhileLocked())
                {

                    return Result<IGrimoireExpiredLeaseAdoptionInterlock>.Failure(
                        LifecycleConflict(
                            "The shared maintenance and adoption interlock already has a process-local owner."));

                }

                _maintenanceAdoptionInterlockOwner = interlock;

            }

            releaseInterlock = false;

            return Result<IGrimoireExpiredLeaseAdoptionInterlock>.Success(interlock);

        }
        finally
        {

            if (releaseInterlock)
            {

                _maintenanceAdoptionInterlock.Release();

            }

        }

    }

    private TaskCompletionSource MaterializeTerminalCallback()
    {

        _ = Interlocked.Increment(ref _materializedTerminalCallbacks);

        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    }

    private static TaskCompletionSource<long> NewOpenGenerationSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static ValueTask AfterSuccessfulDrainNoOpAsync(
        CancellationToken cancellationToken) => ValueTask.CompletedTask;

    private static Error LifecycleConflict(string message) =>
        new(LifecycleConflictCode, message);

    private bool HasLiveFinisherLifetimeWhileLocked(DbConnection connection)
    {

        for (OrdinaryLifetime? lifetime = CurrentOrdinaryLifetime.Value;
            lifetime is not null;
            lifetime = lifetime.Previous)
        {

            if (ReferenceEquals(lifetime.Gate, this)
                && !lifetime.IsReleased
                && lifetime.Generation == _generation
                && (lifetime.PromotedConnection is null
                    || ReferenceEquals(lifetime.PromotedConnection, connection)))
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

    private Result RevalidateAfterNativeOpen(OpenTicket ticket)
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

            ticket.State = OpenTicketState.RevalidatedAfterOpen;

            return Result.Success();

        }

    }

    private Result MarkOpened(OpenTicket ticket)
    {

        lock (_sync)
        {

            ticket.RequireStateWhileLocked(OpenTicketState.RevalidatedAfterOpen);

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

            if (ticket.State is not OpenTicketState.Opening
                and not OpenTicketState.RevalidatedAfterOpen)
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

            if (ticket.State is not OpenTicketState.Terminal)
            {

                throw new InvalidOperationException(
                    "A Grimoire open ticket requires an explicit terminal outcome before disposal.");

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

    private void ResetStageTwoAttempt(Closure closure)
    {

        lock (_sync)
        {

            closure.StageTwoInProgress = false;

        }

    }

    private bool IsClosedLeaseReservationPendingWhileLocked() =>
        _state == GateState.Closed
        && _closure is { StageTwoInProgress: true, ActiveClosedLease: null };

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

    private Result<IGrimoireScopedConnectionPermit> AcquireScopedConnectionPermit(
        ClosedLease lease,
        DbConnection connection)
    {

        ArgumentNullException.ThrowIfNull(connection);

        lock (_sync)
        {

            if (!OwnsClosedLeaseWhileLocked(lease))
            {

                return Result<IGrimoireScopedConnectionPermit>.Failure(
                    LifecycleConflict(
                        "Only the exact live closed Grimoire owner may bind a scoped connection permit."));

            }

            if (lease.Closure.ActiveScopedConnectionPermit is { IsReleased: false })
            {

                return Result<IGrimoireScopedConnectionPermit>.Failure(
                    LifecycleConflict(
                        "The closed Grimoire owner already has a live scoped connection permit."));

            }

            ScopedConnectionPermit permit = new(
                this,
                lease.Closure,
                connection,
                lease.Generation);

            lease.Closure.ActiveScopedConnectionPermit = permit;

            return Result<IGrimoireScopedConnectionPermit>.Success(permit);

        }

    }

    private Result<IGrimoireMaintenanceRenewalTicket> IssueMaintenanceRenewalTicket(
        ClosedLease lease,
        IGrimoireMaintenanceIoLane lane)
    {

        ArgumentNullException.ThrowIfNull(lane);

        lock (_sync)
        {

            if (!OwnsClosedLeaseWhileLocked(lease)
                || lane is not MaintenanceIoLane exactLane
                || !OwnsMaintenanceIoLaneWhileLocked(exactLane)
                || !ReferenceEquals(exactLane.Closure, lease.Closure))
            {

                return Result<IGrimoireMaintenanceRenewalTicket>.Failure(
                    LifecycleConflict(
                        "Only the exact live closed Grimoire owner and maintenance lane may issue a renewal ticket."));

            }

            MaintenanceRenewalTicket ticket = new(
                this,
                lease.Closure,
                lease.Generation,
                exactLane);

            lease.Closure.LiveOneShotAuthorities.Add(ticket);

            return Result<IGrimoireMaintenanceRenewalTicket>.Success(ticket);

        }

    }

    private Result<IGrimoireMaintenanceConnectionCapability>
        IssueMaintenanceConnectionCapability(
            ClosedLease lease,
            CovenantMaintenanceConnectionPurpose purpose,
            IGrimoireMaintenanceIoLane lane)
    {

        ArgumentNullException.ThrowIfNull(lane);

        if (!Enum.IsDefined(purpose))
        {

            throw new ArgumentOutOfRangeException(nameof(purpose));

        }

        // Derived here and nowhere else. A caller that could name the file could name a different
        // one, and the comparison this gate makes on the way back in would then be comparing a
        // caller's value with the same caller's value.
        string canonicalPath = purpose is CovenantMaintenanceConnectionPurpose.IntegrityVerification
            ? _paths.ExportStagingDatabasePath(lease.Closure.Owner.OperationId)
            : _paths.CanonicalDatabasePath;

        CovenantMaintenanceConnectionMode mode = purpose switch
        {

            CovenantMaintenanceConnectionPurpose.IntegrityVerification
                or CovenantMaintenanceConnectionPurpose.ReopenVerification
                or CovenantMaintenanceConnectionPurpose.InventorySnapshot =>
                CovenantMaintenanceConnectionMode.ReadOnly,

            _ => CovenantMaintenanceConnectionMode.ReadWrite,

        };

        lock (_sync)
        {

            if (!OwnsClosedLeaseWhileLocked(lease)
                || lane is not MaintenanceIoLane exactLane
                || !OwnsMaintenanceIoLaneWhileLocked(exactLane)
                || !ReferenceEquals(exactLane.Closure, lease.Closure))
            {

                return Result<IGrimoireMaintenanceConnectionCapability>.Failure(
                    LifecycleConflict(
                        "Only the exact live closed Grimoire owner and maintenance lane may issue a maintenance-open capability."));

            }

            MaintenanceConnectionCapability capability = new(
                this,
                lease.Closure,
                lease.Generation,
                canonicalPath,
                mode,
                purpose,
                exactLane);

            lease.Closure.LiveOneShotAuthorities.Add(capability);

            return Result<IGrimoireMaintenanceConnectionCapability>.Success(capability);

        }

    }

    private async ValueTask<Result<IGrimoireMaintenanceIoLane>>
        AcquireMaintenanceIoLaneAsync(
            ClosedLease lease,
            Func<CovenantExclusiveRecoveryOwner, long, CancellationToken, ValueTask<bool>>
                revalidateDurableOwnerAsync,
            CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(revalidateDurableOwnerAsync);

        cancellationToken.ThrowIfCancellationRequested();

        await _maintenanceAdoptionInterlock
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        bool releaseInterlock = true;

        try
        {

            cancellationToken.ThrowIfCancellationRequested();

            lock (_sync)
            {

                if (!OwnsClosedLeaseWhileLocked(lease))
                {

                    return Result<IGrimoireMaintenanceIoLane>.Failure(
                        LifecycleConflict(
                            "The closed Grimoire owner became stale before it acquired the maintenance I/O lane."));

                }

            }

            bool stillOwned = await revalidateDurableOwnerAsync(
                    lease.Owner,
                    lease.Generation,
                    cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (!stillOwned)
            {

                return Result<IGrimoireMaintenanceIoLane>.Failure(
                    LifecycleConflict(
                        "The durable Covenant owner changed before the maintenance I/O phase could start."));

            }

            MaintenanceIoLane lane = new(
                this,
                lease.Closure,
                lease.Generation);

            lock (_sync)
            {

                if (!OwnsClosedLeaseWhileLocked(lease)
                    || _maintenanceAdoptionInterlockOwner is not null)
                {

                    return Result<IGrimoireMaintenanceIoLane>.Failure(
                        LifecycleConflict(
                            "The closed Grimoire owner changed during durable-owner revalidation."));

                }

                _maintenanceAdoptionInterlockOwner = lane;

            }

            releaseInterlock = false;

            return Result<IGrimoireMaintenanceIoLane>.Success(lane);

        }
        finally
        {

            if (releaseInterlock)
            {

                _maintenanceAdoptionInterlock.Release();

            }

        }

    }

    private async ValueTask<Result> RevalidateMaintenanceIoLaneAsync(
        MaintenanceIoLane lane,
        Func<CovenantExclusiveRecoveryOwner, long, CancellationToken, ValueTask<bool>>
            revalidateDurableOwnerAsync,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(revalidateDurableOwnerAsync);

        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {

            if (!OwnsMaintenanceIoLaneWhileLocked(lane))
            {

                return LifecycleConflict(
                    "The maintenance I/O lane no longer owns the shared adoption interlock.");

            }

        }

        bool stillOwned = await revalidateDurableOwnerAsync(
                lane.Owner,
                lane.Generation,
                cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {

            if (!OwnsMaintenanceIoLaneWhileLocked(lane))
            {

                return LifecycleConflict(
                    "The maintenance I/O lane changed during durable-owner revalidation.");

            }

        }

        return stillOwned
            ? Result.Success()
            : LifecycleConflict(
                "The durable Covenant owner expired or changed during the maintenance I/O phase.");

    }

    private Result<IGrimoireTrackedMaintenanceHandle> AcquireScopedConnectionOpen(
        ScopedConnectionPermit permit,
        DbConnection connection,
        CovenantExclusiveRecoveryOwner owner,
        long generation,
        IGrimoireMaintenanceIoLane lane)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(lane);

        lock (_sync)
        {

            if (lane is not MaintenanceIoLane exactLane
                || !OwnsMaintenanceIoLaneWhileLocked(exactLane)
                || !ReferenceEquals(exactLane.Closure, permit.Closure)
                || permit.IsReleased
                || !ReferenceEquals(permit.Closure.ActiveScopedConnectionPermit, permit)
                || permit.ActiveHandle is not null
                || !ReferenceEquals(permit.Connection, connection)
                || permit.Owner != owner
                || permit.Generation != generation)
            {

                return Result<IGrimoireTrackedMaintenanceHandle>.Failure(
                    LifecycleConflict(
                        "The scoped permit did not match the exact connection, owner, generation, and maintenance lane."));

            }

            TrackedMaintenanceHandle handle = new(
                this,
                permit.Closure,
                exactLane,
                permit);

            permit.ActiveHandle = handle;

            RegisterMaintenanceHandleWhileLocked(handle);

            return Result<IGrimoireTrackedMaintenanceHandle>.Success(handle);

        }

    }

    private Result<IGrimoireTrackedMaintenanceHandle> ConsumeMaintenanceRenewalTicket(
        MaintenanceRenewalTicket ticket,
        CovenantExclusiveRecoveryOwner owner,
        long generation,
        IGrimoireMaintenanceIoLane lane)
    {

        ArgumentNullException.ThrowIfNull(lane);

        lock (_sync)
        {

            if (!SpendOneShotAuthorityWhileLocked(ticket))
            {

                return Result<IGrimoireTrackedMaintenanceHandle>.Failure(
                    LifecycleConflict("This Grimoire renewal ticket has already been consumed or released."));

            }

            if (lane is not MaintenanceIoLane exactLane
                || !OwnsMaintenanceIoLaneWhileLocked(exactLane)
                || !ReferenceEquals(exactLane, ticket.IssuingLane)
                || !ReferenceEquals(exactLane.Closure, ticket.Closure)
                || ticket.Owner != owner
                || ticket.Generation != generation)
            {

                return Result<IGrimoireTrackedMaintenanceHandle>.Failure(
                    LifecycleConflict(
                        "The renewal ticket did not match the exact owner, generation, and maintenance lane."));

            }

            TrackedMaintenanceHandle handle = new(
                this,
                ticket.Closure,
                exactLane,
                scopedPermit: null);

            RegisterMaintenanceHandleWhileLocked(handle);

            return Result<IGrimoireTrackedMaintenanceHandle>.Success(handle);

        }

    }

    private Result<IGrimoireTrackedMaintenanceHandle> ConsumeMaintenanceConnectionCapability(
        MaintenanceConnectionCapability capability,
        CovenantExclusiveRecoveryOwner owner,
        long generation,
        CovenantMaintenanceConnectionPurpose purpose,
        IGrimoireMaintenanceIoLane lane)
    {

        ArgumentNullException.ThrowIfNull(lane);

        lock (_sync)
        {

            if (!SpendOneShotAuthorityWhileLocked(capability))
            {

                return Result<IGrimoireTrackedMaintenanceHandle>.Failure(
                    LifecycleConflict(
                        "This Grimoire maintenance-open capability has already been consumed or released."));

            }

            if (lane is not MaintenanceIoLane exactLane
                || !OwnsMaintenanceIoLaneWhileLocked(exactLane)
                || !ReferenceEquals(exactLane, capability.IssuingLane)
                || !ReferenceEquals(exactLane.Closure, capability.Closure)
                || capability.Owner != owner
                || capability.Generation != generation
                || capability.Purpose != purpose)
            {

                return Result<IGrimoireTrackedMaintenanceHandle>.Failure(
                    LifecycleConflict(
                        "The maintenance-open capability did not match its exact owner, generation, purpose, and lane."));

            }

            TrackedMaintenanceHandle handle = new(
                this,
                capability.Closure,
                exactLane,
                scopedPermit: null);

            RegisterMaintenanceHandleWhileLocked(handle);

            return Result<IGrimoireTrackedMaintenanceHandle>.Success(handle);

        }

    }

    private bool SpendOneShotAuthorityWhileLocked(OneShotAuthority authority)
    {

        if (authority.IsReleased
            || authority.IsConsumed
            || !authority.Closure.LiveOneShotAuthorities.Remove(authority))
        {

            return false;

        }

        authority.IsConsumed = true;

        return true;

    }

    private void RegisterMaintenanceHandleWhileLocked(TrackedMaintenanceHandle handle)
    {

        handle.Closure.LiveMaintenanceHandles.Add(handle);

        handle.Lane.RegisterHandleWhileLocked(handle);

    }

    private Result ReportMaintenanceOpenStarted(TrackedMaintenanceHandle handle)
    {

        lock (_sync)
        {

            if (handle.State != MaintenanceHandleState.NotStarted
                || !handle.Closure.LiveMaintenanceHandles.Contains(handle))
            {

                return LifecycleConflict(
                    "This maintenance handle cannot start another native open from its current state.");

            }

            handle.State = MaintenanceHandleState.OpenStarted;

            return Result.Success();

        }

    }

    private Result ReportMaintenanceNotOpened(TrackedMaintenanceHandle handle)
    {

        lock (_sync)
        {

            if (handle.State != MaintenanceHandleState.NotStarted)
            {

                return LifecycleConflict(
                    "Only a maintenance handle whose native open never started may report not opened.");

            }

            return CompleteMaintenanceHandleWhileLocked(
                handle,
                MaintenanceHandleState.NotOpened);

        }

    }

    private Result ReportMaintenancePhysicallyClosed(TrackedMaintenanceHandle handle)
    {

        lock (_sync)
        {

            if (handle.State != MaintenanceHandleState.OpenStarted)
            {

                return LifecycleConflict(
                    "Physical closure may be reported only after this maintenance handle started native open.");

            }

            return CompleteMaintenanceHandleWhileLocked(
                handle,
                MaintenanceHandleState.PhysicallyClosed);

        }

    }

    private Result CompleteMaintenanceHandleWhileLocked(
        TrackedMaintenanceHandle handle,
        MaintenanceHandleState terminalState)
    {

        if (!handle.Closure.LiveMaintenanceHandles.Remove(handle))
        {

            return LifecycleConflict(
                "This maintenance handle already reported its terminal physical-open state.");

        }

        handle.State = terminalState;

        if (handle.ScopedPermit is not null
            && ReferenceEquals(handle.ScopedPermit.ActiveHandle, handle))
        {

            handle.ScopedPermit.ActiveHandle = null;

            if (handle.ScopedPermit.DisposeRequested)
            {

                ReleaseScopedConnectionPermitWhileLocked(handle.ScopedPermit);

            }

        }

        handle.Lane.ReleaseHandleWhileLocked(handle);

        return Result.Success();

    }

    /// <summary>
    /// Records that a tracked physical-open lifetime was abandoned without reporting an outcome.
    /// </summary>
    /// <remarks>
    /// Disposal is the caller's guard, and a guard may only say what it knows. A handle that already
    /// reported is left exactly as it is. One whose native open never started did not open, and that
    /// is a fact disposal can establish on its own, so it completes.
    ///
    /// <para>A handle whose open had already started is the case this method exists to get right. It
    /// is marked abandoned and left in its closure's live set: nothing here inspected the connection,
    /// so nothing here may report it closed, and reporting one would let ordinary admission reopen
    /// while a maintenance connection is possibly still physically open. Refusing the disposition
    /// instead is the fail-closed reading, and it is the one the whole gate is built on.</para>
    /// </remarks>
    private void CompleteMaintenanceHandleOnDispose(TrackedMaintenanceHandle handle)
    {

        lock (_sync)
        {

            if (!handle.Closure.LiveMaintenanceHandles.Contains(handle))
            {

                return;

            }

            if (handle.State is MaintenanceHandleState.NotStarted)
            {

                _ = CompleteMaintenanceHandleWhileLocked(
                    handle,
                    MaintenanceHandleState.NotOpened);

                return;

            }

            handle.State = MaintenanceHandleState.AbandonedWhileOpen;

        }

    }

    private void ReleaseScopedConnectionPermit(ScopedConnectionPermit permit)
    {

        lock (_sync)
        {

            if (permit.ActiveHandle is not null)
            {

                return;

            }

            ReleaseScopedConnectionPermitWhileLocked(permit);

        }

    }

    private static void ReleaseScopedConnectionPermitWhileLocked(
        ScopedConnectionPermit permit)
    {

        permit.IsReleased = true;

        if (ReferenceEquals(permit.Closure.ActiveScopedConnectionPermit, permit))
        {

            permit.Closure.ActiveScopedConnectionPermit = null;

        }

    }

    private void ReleaseOneShotAuthority(OneShotAuthority authority)
    {

        lock (_sync)
        {

            authority.IsReleased = true;

            _ = authority.Closure.LiveOneShotAuthorities.Remove(authority);

        }

    }

    private async ValueTask ReleaseMaintenanceIoLaneAsync(MaintenanceIoLane lane)
    {

        lock (_sync)
        {

            foreach (OneShotAuthority authority in lane.Closure.LiveOneShotAuthorities
                .Where(candidate => ReferenceEquals(candidate.IssuingLane, lane))
                .ToArray())
            {

                authority.IsReleased = true;

                _ = lane.Closure.LiveOneShotAuthorities.Remove(authority);

            }

        }

        try
        {

            await lane.HandlesDrained
                .WaitAsync(_openingAttemptTimeout, _timeProvider)
                .ConfigureAwait(false);

        }
        catch (TimeoutException)
        {

            // A phase that threw between consuming a capability and reporting its handle's terminal
            // physical-open state leaves that handle live for good, and this wait sits in front of
            // the only code that gives the process-wide adoption interlock back. Waiting forever
            // turns one leaked handle into a wedged process, so the wait is bounded and the release
            // below runs either way. The handles stay live, so the closed lease still refuses to
            // disposition - the leak is reported, not forgiven.

        }

        lock (_sync)
        {

            if (ReferenceEquals(_maintenanceAdoptionInterlockOwner, lane))
            {

                _maintenanceAdoptionInterlockOwner = null;

                _maintenanceAdoptionInterlock.Release();

            }

        }

        lane.SignalReleased();

    }

    private ValueTask ReleaseExpiredLeaseAdoptionInterlockAsync(
        ExpiredLeaseAdoptionInterlock interlock)
    {

        lock (_sync)
        {

            if (ReferenceEquals(_maintenanceAdoptionInterlockOwner, interlock))
            {

                _maintenanceAdoptionInterlockOwner = null;

                _maintenanceAdoptionInterlock.Release();

            }

        }

        return ValueTask.CompletedTask;

    }

    private bool OwnsClosedLeaseWhileLocked(ClosedLease lease) =>
        _state == GateState.Closed
        && !lease.IsReleased
        && !lease.DispositionClaimed
        && ReferenceEquals(_closure, lease.Closure)
        && ReferenceEquals(lease.Closure.ActiveClosedLease, lease)
        && lease.Closure.Owner == lease.Owner
        && lease.Generation == _generation;

    private bool OwnsMaintenanceIoLaneWhileLocked(MaintenanceIoLane lane) =>
        !lane.DisposalStarted
        && ReferenceEquals(_maintenanceAdoptionInterlockOwner, lane)
        && ReferenceEquals(_closure, lane.Closure)
        && lane.Closure.Owner == lane.Owner
        && lane.Generation == _generation;

    private Result CompleteClosedLease(
        ClosedLease lease,
        CovenantExclusiveLeaseDisposition disposition)
    {

        TaskCompletionSource<long>? opened = null;

        long openGeneration = 0;

        lock (_sync)
        {

            if (!OwnsClosedLeaseWhileLocked(lease))
            {

                return LifecycleConflict(
                    "The exclusive Grimoire lease no longer owns its exact closed generation.");

            }

            if (_unresolvedOpens.Count != 0
                || lease.Closure.ActiveScopedConnectionPermit is { IsReleased: false }
                || lease.Closure.LiveOneShotAuthorities.Count != 0
                || lease.Closure.LiveMaintenanceHandles.Count != 0
                || (_maintenanceAdoptionInterlockOwner is MaintenanceIoLane lane
                    && ReferenceEquals(lane.Closure, lease.Closure)))
            {

                return LifecycleConflict(
                    "The closed Grimoire owner cannot disposition while an open ticket or maintenance authority remains live.");

            }

            lease.DispositionClaimed = true;

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

        RevalidatedAfterOpen = 2,

        RefusedAfterOpenRequired = 3,

        Terminal = 4,

    }

    private enum MaintenanceHandleState : byte
    {

        NotStarted = 1,

        OpenStarted = 2,

        NotOpened = 3,

        PhysicallyClosed = 4,

        /// <summary>
        /// A native open that started and whose phase unwound without ever reporting an outcome.
        /// </summary>
        /// <remarks>
        /// Deliberately not a terminal state. Nothing here observed the connection, so nothing here
        /// may claim it closed; the handle stays in its closure's live set and keeps refusing
        /// disposition, which is the fail-closed reading of "a physical open may still be out there".
        /// </remarks>
        AbandonedWhileOpen = 5,

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

        internal ScopedConnectionPermit? ActiveScopedConnectionPermit { get; set; }

        internal HashSet<OneShotAuthority> LiveOneShotAuthorities { get; } = [];

        internal HashSet<TrackedMaintenanceHandle> LiveMaintenanceHandles { get; } = [];

        internal bool StageOneDrained { get; set; }

        internal bool StageOneTimedOut { get; set; }

        internal bool StageTwoInProgress { get; set; }

    }

    private sealed class OrdinaryLifetime(
        GrimoireConnectionAdmissionGate gate,
        long generation,
        OrdinaryLifetime? previous)
    {

        internal GrimoireConnectionAdmissionGate Gate { get; } = gate;

        internal long Generation { get; } = generation;

        internal OrdinaryLifetime? Previous { get; } = previous;

        internal DbConnection? PromotedConnection { get; set; }

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

        private TaskCompletionSource? _terminal;

        private int _disposed;

        internal DbConnection Connection { get; } = connection;

        public long Generation { get; } = generation;

        internal OpenTicketState State { get; set; } = OpenTicketState.Opening;

        internal bool RefusalRequested { get; private set; }

        /// <summary>
        /// The wait a stage-two close takes on this open, built the first time one asks for it.
        /// </summary>
        /// <remarks>
        /// Called only from inside the gate's lock, which is what makes the lazy field safe. A
        /// ticket that has already reached its terminal state has nothing left to wait for, so it
        /// answers with a completed task and builds nothing at all.
        /// </remarks>
        internal Task TerminalCallbackWhileLocked()
        {

            if (State is OpenTicketState.Terminal)
            {

                return Task.CompletedTask;

            }

            _terminal ??= gate.MaterializeTerminalCallback();

            return _terminal.Task;

        }

        public Result RevalidateAfterNativeOpen()
        {

            ThrowIfDisposed();

            return gate.RevalidateAfterNativeOpen(this);

        }

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

            if (Volatile.Read(ref _disposed) != 0)
            {

                return;

            }

            gate.DisposeTicket(this);

            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {

                return;

            }

            GC.SuppressFinalize(this);

        }

        internal void RequestRefusalWhileLocked()
        {

            if (State is OpenTicketState.Opening
                or OpenTicketState.RevalidatedAfterOpen)
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

        internal void SignalTerminalWhileLocked() => _ = _terminal?.TrySetResult();

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

    private sealed class ScopedConnectionPermit(
        GrimoireConnectionAdmissionGate gate,
        Closure closure,
        DbConnection connection,
        long generation) : IGrimoireScopedConnectionPermit
    {

        private int _disposeRequested;

        internal Closure Closure { get; } = closure;

        internal DbConnection Connection { get; } = connection;

        internal CovenantExclusiveRecoveryOwner Owner => Closure.Owner;

        internal long Generation { get; } = generation;

        internal TrackedMaintenanceHandle? ActiveHandle { get; set; }

        internal bool DisposeRequested => Volatile.Read(ref _disposeRequested) != 0;

        internal bool IsReleased { get; set; }

        public Result<IGrimoireTrackedMaintenanceHandle> AcquireOpen(
            DbConnection connection,
            CovenantExclusiveRecoveryOwner owner,
            long generation,
            IGrimoireMaintenanceIoLane lane) =>
            gate.AcquireScopedConnectionOpen(
                this,
                connection,
                owner,
                generation,
                lane);

        public ValueTask DisposeAsync()
        {

            if (Interlocked.Exchange(ref _disposeRequested, 1) == 0)
            {

                gate.ReleaseScopedConnectionPermit(this);

            }

            GC.SuppressFinalize(this);

            return ValueTask.CompletedTask;

        }

    }

    private abstract class OneShotAuthority(
        GrimoireConnectionAdmissionGate gate,
        Closure closure,
        long generation,
        MaintenanceIoLane issuingLane) : IAsyncDisposable
    {

        private int _disposeRequested;

        protected GrimoireConnectionAdmissionGate Gate { get; } = gate;

        internal Closure Closure { get; } = closure;

        internal CovenantExclusiveRecoveryOwner Owner => Closure.Owner;

        internal long Generation { get; } = generation;

        internal MaintenanceIoLane IssuingLane { get; } = issuingLane;

        internal bool IsConsumed { get; set; }

        internal bool IsReleased { get; set; }

        public ValueTask DisposeAsync()
        {

            if (Interlocked.Exchange(ref _disposeRequested, 1) == 0)
            {

                Gate.ReleaseOneShotAuthority(this);

            }

            GC.SuppressFinalize(this);

            return ValueTask.CompletedTask;

        }

    }

    private sealed class MaintenanceRenewalTicket(
        GrimoireConnectionAdmissionGate gate,
        Closure closure,
        long generation,
        MaintenanceIoLane issuingLane) :
        OneShotAuthority(gate, closure, generation, issuingLane),
        IGrimoireMaintenanceRenewalTicket
    {

        public Result<IGrimoireTrackedMaintenanceHandle> Consume(
            CovenantExclusiveRecoveryOwner owner,
            long generation,
            IGrimoireMaintenanceIoLane lane) =>
            Gate.ConsumeMaintenanceRenewalTicket(
                this,
                owner,
                generation,
                lane);

    }

    private sealed class MaintenanceConnectionCapability(
        GrimoireConnectionAdmissionGate gate,
        Closure closure,
        long generation,
        string canonicalPath,
        CovenantMaintenanceConnectionMode mode,
        CovenantMaintenanceConnectionPurpose purpose,
        MaintenanceIoLane issuingLane) :
        OneShotAuthority(gate, closure, generation, issuingLane),
        IGrimoireMaintenanceConnectionCapability
    {

        public string CanonicalPath { get; } = canonicalPath;

        public CovenantMaintenanceConnectionMode Mode { get; } = mode;

        public CovenantMaintenanceConnectionPurpose Purpose { get; } = purpose;

        public Result<IGrimoireTrackedMaintenanceHandle> Consume(
            CovenantExclusiveRecoveryOwner owner,
            long generation,
            CovenantMaintenanceConnectionPurpose purpose,
            IGrimoireMaintenanceIoLane lane) =>
            Gate.ConsumeMaintenanceConnectionCapability(
                this,
                owner,
                generation,
                purpose,
                lane);

    }

    private sealed class TrackedMaintenanceHandle(
        GrimoireConnectionAdmissionGate gate,
        Closure closure,
        MaintenanceIoLane lane,
        ScopedConnectionPermit? scopedPermit) : IGrimoireTrackedMaintenanceHandle
    {

        private int _disposed;

        internal Closure Closure { get; } = closure;

        internal MaintenanceIoLane Lane { get; } = lane;

        internal ScopedConnectionPermit? ScopedPermit { get; } = scopedPermit;

        internal MaintenanceHandleState State { get; set; } =
            MaintenanceHandleState.NotStarted;

        public Result ReportOpenStarted() => gate.ReportMaintenanceOpenStarted(this);

        public Result ReportNotOpened() => gate.ReportMaintenanceNotOpened(this);

        public Result ReportPhysicallyClosed() =>
            gate.ReportMaintenancePhysicallyClosed(this);

        public ValueTask DisposeAsync()
        {

            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {

                gate.CompleteMaintenanceHandleOnDispose(this);

            }

            GC.SuppressFinalize(this);

            return ValueTask.CompletedTask;

        }

    }

    private sealed class MaintenanceIoLane(
        GrimoireConnectionAdmissionGate gate,
        Closure closure,
        long generation) : IGrimoireMaintenanceIoLane
    {

        private readonly HashSet<TrackedMaintenanceHandle> _liveHandles = [];

        private readonly TaskCompletionSource _released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource _handlesDrained = CompletedSignal();

        private int _disposalStarted;

        internal Closure Closure { get; } = closure;

        internal bool DisposalStarted => Volatile.Read(ref _disposalStarted) != 0;

        internal Task HandlesDrained => _handlesDrained.Task;

        public CovenantExclusiveRecoveryOwner Owner => Closure.Owner;

        public long Generation { get; } = generation;

        public ValueTask<Result> RevalidateDurableOwnerAsync(
            Func<CovenantExclusiveRecoveryOwner, long, CancellationToken, ValueTask<bool>>
                revalidateDurableOwnerAsync,
            CancellationToken cancellationToken) =>
            gate.RevalidateMaintenanceIoLaneAsync(
                this,
                revalidateDurableOwnerAsync,
                cancellationToken);

        public ValueTask DisposeAsync()
        {

            if (Interlocked.Exchange(ref _disposalStarted, 1) == 0)
            {

                return gate.ReleaseMaintenanceIoLaneAsync(this);

            }

            return new ValueTask(_released.Task);

        }

        internal void RegisterHandleWhileLocked(TrackedMaintenanceHandle handle)
        {

            if (_liveHandles.Count == 0)
            {

                _handlesDrained = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            }

            _liveHandles.Add(handle);

        }

        internal void ReleaseHandleWhileLocked(TrackedMaintenanceHandle handle)
        {

            _ = _liveHandles.Remove(handle);

            if (_liveHandles.Count == 0)
            {

                _handlesDrained.TrySetResult();

            }

        }

        internal void SignalReleased()
        {

            _released.TrySetResult();

            GC.SuppressFinalize(this);

        }

        private static TaskCompletionSource CompletedSignal()
        {

            TaskCompletionSource completion = new(
                TaskCreationOptions.RunContinuationsAsynchronously);

            completion.TrySetResult();

            return completion;

        }

    }

    private sealed class ExpiredLeaseAdoptionInterlock(
        GrimoireConnectionAdmissionGate gate,
        CovenantExclusiveRecoveryOwner candidateOwner) :
        IGrimoireExpiredLeaseAdoptionInterlock
    {

        private int _released;

        public CovenantExclusiveRecoveryOwner CandidateOwner { get; } = candidateOwner;

        public ValueTask DisposeAsync()
        {

            if (Interlocked.Exchange(ref _released, 1) != 0)
            {

                return ValueTask.CompletedTask;

            }

            GC.SuppressFinalize(this);

            return gate.ReleaseExpiredLeaseAdoptionInterlockAsync(this);

        }

    }

    private sealed class ClosedLease(
        GrimoireConnectionAdmissionGate gate,
        Closure closure,
        long generation) : IGrimoireExclusiveClosedLease
    {

        private int _released;

        internal Closure Closure { get; } = closure;

        internal bool DispositionClaimed { get; set; }

        internal bool IsReleased => Volatile.Read(ref _released) != 0;

        public CovenantExclusiveRecoveryOwner Owner => Closure.Owner;

        public long Generation { get; } = generation;

        public Result<IGrimoireScopedConnectionPermit> AcquireScopedConnectionPermit(
            DbConnection connection) =>
            gate.AcquireScopedConnectionPermit(this, connection);

        public Result<IGrimoireMaintenanceRenewalTicket> IssueMaintenanceRenewalTicket(
            IGrimoireMaintenanceIoLane lane) =>
            gate.IssueMaintenanceRenewalTicket(this, lane);

        public Result<IGrimoireMaintenanceConnectionCapability>
            IssueMaintenanceConnectionCapability(
                CovenantMaintenanceConnectionPurpose purpose,
                IGrimoireMaintenanceIoLane lane) =>
            gate.IssueMaintenanceConnectionCapability(this, purpose, lane);

        public ValueTask<Result<IGrimoireMaintenanceIoLane>> AcquireMaintenanceIoLaneAsync(
            Func<CovenantExclusiveRecoveryOwner, long, CancellationToken, ValueTask<bool>>
                revalidateDurableOwnerAsync,
            CancellationToken cancellationToken) =>
            gate.AcquireMaintenanceIoLaneAsync(
                this,
                revalidateDurableOwnerAsync,
                cancellationToken);

        // The token is accepted for symmetry with the rest of the lease surface and is deliberately
        // not observed. CompleteClosedLease is the only edge from Closed back to Ordinary and it is
        // a pure in-lock state transition with no I/O to abandon, so there is nothing here that
        // cancelling could save - only a wedged gate, because a cleanup path unwinding under an
        // ambient shutdown token is exactly the caller that must still be allowed to reopen.
        public ValueTask<Result> CompleteAsync(
            CovenantExclusiveLeaseDisposition disposition,
            CancellationToken cancellationToken)
        {

            _ = cancellationToken;

            if (disposition is not CovenantExclusiveLeaseDisposition.RollbackAndReopen
                and not CovenantExclusiveLeaseDisposition.CommitAndReopen
                and not CovenantExclusiveLeaseDisposition.KeepClosed)
            {

                throw new ArgumentOutOfRangeException(nameof(disposition));

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
