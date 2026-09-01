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

    private readonly SemaphoreSlim _maintenanceAdoptionInterlock = new(1, 1);

    private GateState _state = GateState.Ordinary;

    private long _generation = 1;

    private Closure? _closure;

    private object? _maintenanceAdoptionInterlockOwner;

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

                if (_maintenanceAdoptionInterlockOwner is not null)
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

            if (ticket.State is OpenTicketState.Opening
                or OpenTicketState.RevalidatedAfterOpen)
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
            string canonicalPath,
            CovenantMaintenanceConnectionMode mode,
            CovenantMaintenanceConnectionPurpose purpose,
            IGrimoireMaintenanceIoLane lane)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);

        ArgumentNullException.ThrowIfNull(lane);

        if (mode is not CovenantMaintenanceConnectionMode.ReadOnly
            and not CovenantMaintenanceConnectionMode.ReadWrite)
        {

            throw new ArgumentOutOfRangeException(nameof(mode));

        }

        if (purpose is not CovenantMaintenanceConnectionPurpose.CanonicalErasure
            and not CovenantMaintenanceConnectionPurpose.Compaction
            and not CovenantMaintenanceConnectionPurpose.IntegrityVerification
            and not CovenantMaintenanceConnectionPurpose.SidecarProof
            and not CovenantMaintenanceConnectionPurpose.ReopenVerification)
        {

            throw new ArgumentOutOfRangeException(nameof(purpose));

        }

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
        string canonicalPath,
        CovenantMaintenanceConnectionMode mode,
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
                || !StringComparer.Ordinal.Equals(capability.CanonicalPath, canonicalPath)
                || capability.Mode != mode
                || capability.Purpose != purpose)
            {

                return Result<IGrimoireTrackedMaintenanceHandle>.Failure(
                    LifecycleConflict(
                        "The maintenance-open capability did not match its exact owner, generation, canonical path, mode, purpose, and lane."));

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

        await lane.HandlesDrained.ConfigureAwait(false);

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
                || lease.Closure.LiveMaintenanceHandles.Count != 0)
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

            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {

                return;

            }

            gate.DisposeTicket(this);

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

        internal string CanonicalPath { get; } = canonicalPath;

        internal CovenantMaintenanceConnectionMode Mode { get; } = mode;

        internal CovenantMaintenanceConnectionPurpose Purpose { get; } = purpose;

        public Result<IGrimoireTrackedMaintenanceHandle> Consume(
            CovenantExclusiveRecoveryOwner owner,
            long generation,
            string canonicalPath,
            CovenantMaintenanceConnectionMode mode,
            CovenantMaintenanceConnectionPurpose purpose,
            IGrimoireMaintenanceIoLane lane) =>
            Gate.ConsumeMaintenanceConnectionCapability(
                this,
                owner,
                generation,
                canonicalPath,
                mode,
                purpose,
                lane);

    }

    private sealed class TrackedMaintenanceHandle(
        GrimoireConnectionAdmissionGate gate,
        Closure closure,
        MaintenanceIoLane lane,
        ScopedConnectionPermit? scopedPermit) : IGrimoireTrackedMaintenanceHandle
    {

        internal Closure Closure { get; } = closure;

        internal MaintenanceIoLane Lane { get; } = lane;

        internal ScopedConnectionPermit? ScopedPermit { get; } = scopedPermit;

        internal MaintenanceHandleState State { get; set; } =
            MaintenanceHandleState.NotStarted;

        public Result ReportOpenStarted() => gate.ReportMaintenanceOpenStarted(this);

        public Result ReportNotOpened() => gate.ReportMaintenanceNotOpened(this);

        public Result ReportPhysicallyClosed() =>
            gate.ReportMaintenancePhysicallyClosed(this);

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
                string canonicalPath,
                CovenantMaintenanceConnectionMode mode,
                CovenantMaintenanceConnectionPurpose purpose,
                IGrimoireMaintenanceIoLane lane) =>
            gate.IssueMaintenanceConnectionCapability(
                this,
                canonicalPath,
                mode,
                purpose,
                lane);

        public ValueTask<Result<IGrimoireMaintenanceIoLane>> AcquireMaintenanceIoLaneAsync(
            Func<CovenantExclusiveRecoveryOwner, long, CancellationToken, ValueTask<bool>>
                revalidateDurableOwnerAsync,
            CancellationToken cancellationToken) =>
            gate.AcquireMaintenanceIoLaneAsync(
                this,
                revalidateDurableOwnerAsync,
                cancellationToken);

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
