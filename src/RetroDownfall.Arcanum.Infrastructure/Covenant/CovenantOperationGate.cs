using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

/// <summary>
/// The process-wide implementation of <see cref="ICovenantOperationGate"/>.
/// </summary>
/// <remarks>
/// One lock protects one small map of live registrations and at most three closure slots. Acquiring
/// and revalidating touch memory only: no secret store, no database, no logging of a Campaign ID or
/// any Covenant content.
///
/// <para>Closing is the interesting half. An exclusive acquisition atomically records its recovery
/// owner and rejects new affected work <em>before</em> it starts waiting, then cancels and drains the
/// registrations that were already live. Doing it in the other order would leave a permanent race in
/// which new readers keep arriving faster than old ones leave and a reset never gets to run.</para>
///
/// <para>Cancellation happens outside the lock. <see cref="CancellationTokenSource.Cancel()"/> runs
/// registered callbacks synchronously on the calling thread, and a consumer callback that reached
/// back into the gate would deadlock against a lock the canceller still held.</para>
/// </remarks>
internal sealed class CovenantOperationGate : ICovenantOperationGate
{

    private static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(30);

    private readonly CovenantRuntimeGenerationProvider _runtime;

    private readonly ICovenantCampaignScopeProbe _campaigns;

    private readonly TimeSpan _drainTimeout;

    private readonly Lock _sync = new();

    private readonly Dictionary<Guid, ScopedRegistration> _registrations = [];

    private readonly Dictionary<Guid, Closure> _campaignClosures = [];

    private Closure? _installationClosure;

    private Closure? _globalScopeClosure;

    private bool _readinessPublished;

    internal CovenantOperationGate(
        CovenantRuntimeGenerationProvider runtime,
        ICovenantCampaignScopeProbe campaigns,
        TimeSpan? drainTimeout = null)
    {

        ArgumentNullException.ThrowIfNull(runtime);

        ArgumentNullException.ThrowIfNull(campaigns);

        _runtime = runtime;

        _campaigns = campaigns;

        _drainTimeout = drainTimeout ?? DefaultDrainTimeout;

    }

    /// <summary>
    /// Live ordinary registrations. Exclusive registrations are tracked on their closure instead, so
    /// this is exactly the set a close has to drain.
    /// </summary>
    internal int LiveRegistrationCount
    {

        get
        {

            lock (_sync)
            {

                return _registrations.Count;

            }

        }

    }

    public ValueTask<Result<CovenantInstallationReadLease>> AcquireInstallationReadAsync(
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            AcquireOrdinary(
                CovenantLeaseKind.InstallationRead,
                scope: null,
                campaignAvailabilityGeneration: null,
                campaignPathRevision: null,
                cancellationToken,
                static registration => new CovenantInstallationReadLease(registration)));

    public ValueTask<Result<CovenantReadLease>> AcquireReadAsync(
        CovenantOperationScope scope,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            AcquireOrdinary(
                CovenantLeaseKind.Read,
                scope,
                campaignAvailabilityGeneration: null,
                campaignPathRevision: null,
                cancellationToken,
                static registration => new CovenantReadLease(registration)));

    public ValueTask<Result<CovenantWriteLease>> AcquireWriteAsync(
        CovenantOperationScope scope,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            AcquireOrdinary(
                CovenantLeaseKind.Write,
                scope,
                campaignAvailabilityGeneration: null,
                campaignPathRevision: null,
                cancellationToken,
                static registration => new CovenantWriteLease(registration)));

    public ValueTask<Result<CovenantTurnLease>> AcquireTurnAsync(
        CanonicalCampaignContext campaign,
        CancellationToken cancellationToken)
    {

        CovenantOperationScope scope = campaign.IsCampaignBound
            ? CovenantOperationScope.ForCampaign(campaign.CampaignId!.Value)
            : CovenantOperationScope.Global;

        return ValueTask.FromResult(
            AcquireOrdinary(
                CovenantLeaseKind.Turn,
                scope,
                campaign.CampaignAvailabilityGeneration,
                campaign.PathIdentityRevision,
                cancellationToken,
                static registration => new CovenantTurnLease(registration)));

    }

    public ValueTask<Result<CovenantMcpLease>> AcquireMcpAsync(
        CovenantOperationScope scope,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            AcquireOrdinary(
                CovenantLeaseKind.Mcp,
                scope,
                campaignAvailabilityGeneration: null,
                campaignPathRevision: null,
                cancellationToken,
                static registration => new CovenantMcpLease(registration)));

    public ValueTask<Result<CovenantAcceleratorLease>> AcquireAcceleratorAsync(
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            AcquireOrdinary(
                CovenantLeaseKind.Accelerator,
                scope: null,
                campaignAvailabilityGeneration: null,
                campaignPathRevision: null,
                cancellationToken,
                static registration => new CovenantAcceleratorLease(registration)));

    public ValueTask<Result<CovenantCleanupLease>> AcquireCleanupAsync(
        CovenantOperationScope scope,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            AcquireOrdinary(
                CovenantLeaseKind.Cleanup,
                scope,
                campaignAvailabilityGeneration: null,
                campaignPathRevision: null,
                cancellationToken,
                static registration => new CovenantCleanupLease(registration)));

    public async ValueTask<Result<CovenantCampaignExclusiveLease>> AcquireCampaignExclusiveAsync(
        Guid campaignId,
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken)
    {

        if (owner.Operation is not CovenantExclusiveOperation.CampaignPathMutation
            and not CovenantExclusiveOperation.CampaignDelete)
        {

            return ForbiddenOperationShape();

        }

        if (campaignId == Guid.Empty)
        {

            return new Error(ErrorCodes.Covenant.InvalidScope, "A Campaign-exclusive operation requires a Campaign identity.");

        }

        Result campaignPresent = await RequireLiveCampaignAsync(campaignId, cancellationToken).ConfigureAwait(false);

        if (campaignPresent.IsFailure)
        {

            return campaignPresent.Error;

        }

        return await AcquireExclusiveCoreAsync(
                ClosureSlot.Campaign,
                CovenantLeaseKind.CampaignExclusive,
                CovenantOperationScope.ForCampaign(campaignId),
                owner,
                cancellationToken,
                static registration => new CovenantCampaignExclusiveLease(registration))
            .ConfigureAwait(false);

    }

    public async ValueTask<Result<CovenantProtectedTransferLease>> AcquireProtectedTransferAsync(
        ProtectedTransferScope scope,
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken)
    {

        if (owner.Operation != CovenantExclusiveOperation.ProtectedSessionTransfer)
        {

            return ForbiddenOperationShape();

        }

        CovenantOperationScope operationScope = scope.ToOperationScope();

        if (operationScope.Kind == CovenantScope.Campaign)
        {

            Result campaignPresent = await RequireLiveCampaignAsync(
                    operationScope.CampaignId!.Value,
                    cancellationToken)
                .ConfigureAwait(false);

            if (campaignPresent.IsFailure)
            {

                return campaignPresent.Error;

            }

        }

        return await AcquireExclusiveCoreAsync(
                operationScope.Kind == CovenantScope.Global ? ClosureSlot.GlobalScope : ClosureSlot.Campaign,
                CovenantLeaseKind.ProtectedTransfer,
                operationScope,
                owner,
                cancellationToken,
                static registration => new CovenantProtectedTransferLease(registration))
            .ConfigureAwait(false);

    }

    public ValueTask<Result<CovenantExclusiveLease>> AcquireExclusiveAsync(
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken)
    {

        if (owner.Operation is CovenantExclusiveOperation.CampaignPathMutation
            or CovenantExclusiveOperation.CampaignDelete
            or CovenantExclusiveOperation.ProtectedSessionTransfer)
        {

            return ValueTask.FromResult<Result<CovenantExclusiveLease>>(ForbiddenOperationShape());

        }

        return AcquireExclusiveCoreAsync(
            ClosureSlot.Installation,
            CovenantLeaseKind.Exclusive,
            scope: null,
            owner,
            cancellationToken,
            static registration => new CovenantExclusiveLease(registration));

    }

    public ValueTask<Result<CovenantExclusiveLease>> ResumeOrAcquireExclusiveAsync(
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken)
    {

        if (owner.Operation is CovenantExclusiveOperation.CampaignPathMutation
            or CovenantExclusiveOperation.CampaignDelete
            or CovenantExclusiveOperation.ProtectedSessionTransfer)
        {

            return ValueTask.FromResult<Result<CovenantExclusiveLease>>(ForbiddenOperationShape());

        }

        return ResumeOrAcquireInstallationAsync(owner, cancellationToken);

    }

    public ValueTask<Result<CovenantCampaignExclusiveLease>> ResumeCampaignExclusiveAsync(
        Guid campaignId,
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        if (owner.Operation is not CovenantExclusiveOperation.CampaignPathMutation
            and not CovenantExclusiveOperation.CampaignDelete)
        {

            return ValueTask.FromResult<Result<CovenantCampaignExclusiveLease>>(ForbiddenOperationShape());

        }

        return ValueTask.FromResult(
            Resume(
                ClosureSlot.Campaign,
                campaignId,
                owner,
                static registration => new CovenantCampaignExclusiveLease(registration)));

    }

    public ValueTask<Result<CovenantProtectedTransferLease>> ResumeProtectedTransferAsync(
        ProtectedTransferScope scope,
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        if (owner.Operation != CovenantExclusiveOperation.ProtectedSessionTransfer)
        {

            return ValueTask.FromResult<Result<CovenantProtectedTransferLease>>(ForbiddenOperationShape());

        }

        CovenantOperationScope operationScope = scope.ToOperationScope();

        return ValueTask.FromResult(
            Resume(
                operationScope.Kind == CovenantScope.Global ? ClosureSlot.GlobalScope : ClosureSlot.Campaign,
                operationScope.CampaignId,
                owner,
                static registration => new CovenantProtectedTransferLease(registration)));

    }

    public ValueTask<Result<CovenantExclusiveLease>> ResumeExclusiveAsync(
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        if (owner.Operation is CovenantExclusiveOperation.CampaignPathMutation
            or CovenantExclusiveOperation.CampaignDelete
            or CovenantExclusiveOperation.ProtectedSessionTransfer)
        {

            return ValueTask.FromResult<Result<CovenantExclusiveLease>>(ForbiddenOperationShape());

        }

        return ValueTask.FromResult(
            Resume(
                ClosureSlot.Installation,
                campaignId: null,
                owner,
                static registration => new CovenantExclusiveLease(registration)));

    }

    /// <summary>
    /// Reconstructs a kept-closed recovery owner from a validated durable journal during the
    /// pre-readiness recovery phase.
    /// </summary>
    /// <remarks>
    /// Caller-validated on purpose. The gate holds no journal and cannot re-read one; the bootstrap
    /// path that owns the journal proves the owner, and this method records it. Once readiness is
    /// published the absence of a closed owner is an answer, not a gap to be filled, so adoption is
    /// refused outright rather than quietly succeeding.
    /// </remarks>
    internal void AdoptDurableRecoveryOwner(
        CovenantExclusiveRecoveryOwner owner,
        CovenantOperationScope? scope,
        bool cleanupOnlyHistoricalCampaign)
    {

        if (!owner.IsValid)
        {

            throw new ArgumentException("A durable recovery owner must be fully populated.", nameof(owner));

        }

        (ClosureSlot slot, CovenantLeaseKind leaseKind) = ClassifyOwner(owner, scope);

        lock (_sync)
        {

            if (_readinessPublished)
            {

                throw new InvalidOperationException(
                    "Covenant recovery ownership cannot be reconstructed after readiness has been published.");

            }

            Closure closure = new(owner, slot, scope, leaseKind)
            {

                CleanupOnlyHistoricalCampaign = cleanupOnlyHistoricalCampaign,

            };

            if (!TryInstallClosure(closure))
            {

                throw new InvalidOperationException(
                    "Another Covenant recovery owner already holds this scope.");

            }

        }

    }

    /// <summary>
    /// Ends the pre-readiness recovery phase. After this, only a kept-closed owner recorded in this
    /// process can be resumed.
    /// </summary>
    internal void PublishReadiness()
    {

        lock (_sync)
        {

            _readinessPublished = true;

        }

    }

    private static Error ForbiddenOperationShape() =>
        new(
            ErrorCodes.Covenant.ForbiddenAuthority,
            "This exclusive Covenant operation code cannot be used with this acquisition shape.");

    private static (ClosureSlot Slot, CovenantLeaseKind LeaseKind) ClassifyOwner(
        CovenantExclusiveRecoveryOwner owner,
        CovenantOperationScope? scope)
    {

        switch (owner.Operation)
        {

            case CovenantExclusiveOperation.CampaignPathMutation:
            case CovenantExclusiveOperation.CampaignDelete:

                if (scope is not { Kind: CovenantScope.Campaign })
                {

                    throw new ArgumentException(
                        "A Campaign-exclusive recovery owner requires a Campaign scope.",
                        nameof(scope));

                }

                return (ClosureSlot.Campaign, CovenantLeaseKind.CampaignExclusive);

            case CovenantExclusiveOperation.ProtectedSessionTransfer:

                if (scope is not { IsInitialized: true })
                {

                    throw new ArgumentException(
                        "A protected-transfer recovery owner requires a validated transfer scope.",
                        nameof(scope));

                }

                return (
                    scope.Value.Kind == CovenantScope.Global ? ClosureSlot.GlobalScope : ClosureSlot.Campaign,
                    CovenantLeaseKind.ProtectedTransfer);

            default:

                if (scope is not null)
                {

                    throw new ArgumentException(
                        "An installation-wide recovery owner carries no persisted scope.",
                        nameof(scope));

                }

                return (ClosureSlot.Installation, CovenantLeaseKind.Exclusive);

        }

    }

    private async ValueTask<Result> RequireLiveCampaignAsync(
        Guid campaignId,
        CancellationToken cancellationToken)
    {

        Result<CovenantCampaignScopeState> state = await _campaigns
            .ResolveAsync(campaignId, cancellationToken)
            .ConfigureAwait(false);

        if (state.IsFailure)
        {

            return state.Error;

        }

        return state.Value == CovenantCampaignScopeState.Live
            ? Result.Success()
            : new Error(
                ErrorCodes.Covenant.NotFound,
                "The Campaign this exclusive operation names is not live on this installation.");

    }

    private Result<TLease> AcquireOrdinary<TLease>(
        CovenantLeaseKind kind,
        CovenantOperationScope? scope,
        long? campaignAvailabilityGeneration,
        long? campaignPathRevision,
        CancellationToken cancellationToken,
        Func<ICovenantLeaseRegistration, TLease> create)
        where TLease : CovenantOperationLease
    {

        cancellationToken.ThrowIfCancellationRequested();

        if (scope is { IsInitialized: false })
        {

            return new Error(ErrorCodes.Covenant.InvalidScope, "An uninitialized Covenant operation scope cannot be leased.");

        }

        Result<GateFacts> facts = CaptureFacts(requireCanonical: true);

        if (facts.IsFailure)
        {

            return facts.Error;

        }

        CovenantLeaseCoverage coverage = scope is null
            ? CovenantLeaseCoverage.Installation
            : CovenantLeaseCoverage.Scoped;

        ScopedRegistration registration;

        lock (_sync)
        {

            if (IsAcquisitionBlocked(coverage, scope))
            {

                return new Error(
                    ErrorCodes.Covenant.Unavailable,
                    "A Covenant operation is closing this scope, so no new lease may be taken over it.");

            }

            registration = new ScopedRegistration(
                this,
                new CovenantOperationLeaseSnapshot(
                    RegistrationId: Guid.NewGuid(),
                    RuntimeAuthorityGeneration: facts.Value.RuntimeAuthorityGeneration,
                    Kind: kind,
                    Coverage: coverage,
                    Scope: scope,
                    DatasetGeneration: facts.Value.DatasetGeneration,
                    CapabilityGeneration: facts.Value.CapabilityGeneration,
                    AuthorityEpoch: facts.Value.AuthorityEpoch,
                    CanonicalSequence: facts.Value.CanonicalSequence,
                    CampaignAvailabilityGeneration: campaignAvailabilityGeneration,
                    CampaignPathRevision: campaignPathRevision,
                    AcceleratorEpoch: kind == CovenantLeaseKind.Accelerator
                        ? facts.Value.AcceleratorEpoch
                        : null,
                    AppliedCampaignDeletionSequence: kind == CovenantLeaseKind.Accelerator
                        ? facts.Value.AppliedCampaignDeletionSequence
                        : null,
                    RecoveryOwner: null,
                    CleanupOnlyHistoricalCampaign: false));

            _registrations.Add(registration.Snapshot.RegistrationId, registration);

        }

        return create(registration);

    }

    private async ValueTask<Result<TLease>> AcquireExclusiveCoreAsync<TLease>(
        ClosureSlot slot,
        CovenantLeaseKind leaseKind,
        CovenantOperationScope? scope,
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken,
        Func<ICovenantExclusiveLeaseRegistration, TLease> create)
        where TLease : CovenantExclusiveOperationLease
    {

        cancellationToken.ThrowIfCancellationRequested();

        // An exclusive operation may be exactly the thing that repairs an unhealthy canonical tier,
        // so it requires established authority but not a healthy tier.
        Result<GateFacts> facts = CaptureFacts(requireCanonical: false);

        if (facts.IsFailure)
        {

            return facts.Error;

        }

        Closure closure = new(owner, slot, scope, leaseKind);

        closure.AcquisitionInProgress = true;

        List<ScopedRegistration> draining;

        lock (_sync)
        {

            if (!TryInstallClosure(closure))
            {

                return new Error(
                    ErrorCodes.Covenant.Unavailable,
                    "Another Covenant operation already owns this scope.");

            }

            draining = [.. _registrations.Values.Where(closure.Covers)];

        }

        // Outside the lock: cancellation runs consumer callbacks synchronously.
        foreach (ScopedRegistration registration in draining)
        {

            registration.RequestRevocation();

        }

        bool drained = await DrainAsync(draining, cancellationToken).ConfigureAwait(false);

        if (!drained)
        {

            lock (_sync)
            {

                RemoveClosure(closure);

            }

            return new Error(
                ErrorCodes.Covenant.MaintenanceFailed,
                "Covenant leases over this scope did not drain within the operation bound, so nothing was changed.");

        }

        lock (_sync)
        {

            ExclusiveRegistration registration = new(
                this,
                closure,
                BuildExclusiveSnapshot(closure, facts.Value));

            closure.LiveRegistration = registration;

            closure.AcquisitionInProgress = false;

            return create(registration);

        }

    }

    private Result<TLease> Resume<TLease>(
        ClosureSlot slot,
        Guid? campaignId,
        CovenantExclusiveRecoveryOwner owner,
        Func<ICovenantExclusiveLeaseRegistration, TLease> create)
        where TLease : CovenantExclusiveOperationLease
    {

        lock (_sync)
        {

            Closure? closure = FindClosure(slot, campaignId);

            if (closure is null || closure.Owner != owner)
            {

                return new Error(
                    ErrorCodes.Covenant.ManualRecoveryRequired,
                    "No Covenant operation with this exact recovery identity holds this scope closed.");

            }

            if (closure.LiveRegistration is not null || closure.AcquisitionInProgress)
            {

                return new Error(
                    ErrorCodes.Covenant.LifecycleConflict,
                    "This closed Covenant scope already has a live recovery lease.");

            }

            // Match the gate's exact durable closure before consulting the runtime's retired marker.
            // This keeps retired durable counters unreachable to a caller that merely guessed an owner.
            Result<GateFacts> facts = CaptureFacts(requireCanonical: false, owner);

            if (facts.IsFailure)
            {

                return facts.Error;

            }

            ExclusiveRegistration registration = new(
                this,
                closure,
                BuildExclusiveSnapshot(closure, facts.Value));

            closure.LiveRegistration = registration;

            return create(registration);

        }

    }

    private async ValueTask<Result<CovenantExclusiveLease>> ResumeOrAcquireInstallationAsync(
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        Result<GateFacts> acquisitionFacts;

        Closure acquiredClosure;

        List<ScopedRegistration> draining;

        lock (_sync)
        {

            Closure? existing = FindClosure(ClosureSlot.Installation, campaignId: null);

            if (existing is not null)
            {

                if (existing.Owner != owner)
                {

                    return new Error(
                        ErrorCodes.Covenant.ManualRecoveryRequired,
                        "Another Covenant recovery owner already holds this scope closed.");

                }

                if (existing.LiveRegistration is not null || existing.AcquisitionInProgress)
                {

                    return new Error(
                        ErrorCodes.Covenant.LifecycleConflict,
                        "This closed Covenant scope already has a live recovery lease.");

                }

                Result<GateFacts> recoveryFacts = CaptureFacts(requireCanonical: false, owner);

                if (recoveryFacts.IsFailure)
                {

                    return recoveryFacts.Error;

                }

                ExclusiveRegistration resumed = new(
                    this,
                    existing,
                    BuildExclusiveSnapshot(existing, recoveryFacts.Value));

                existing.LiveRegistration = resumed;

                return new CovenantExclusiveLease(resumed);

            }

            acquisitionFacts = CaptureFacts(requireCanonical: false);

            if (acquisitionFacts.IsFailure)
            {

                return acquisitionFacts.Error;

            }

            acquiredClosure = new Closure(
                owner,
                ClosureSlot.Installation,
                scope: null,
                CovenantLeaseKind.Exclusive)
            {

                AcquisitionInProgress = true,

            };

            if (!TryInstallClosure(acquiredClosure))
            {

                return new Error(
                    ErrorCodes.Covenant.Unavailable,
                    "Another Covenant operation already owns this scope.");

            }

            draining = [.. _registrations.Values.Where(acquiredClosure.Covers)];

        }

        foreach (ScopedRegistration registration in draining)
        {

            registration.RequestRevocation();

        }

        bool drained = await DrainAsync(draining, cancellationToken).ConfigureAwait(false);

        if (!drained)
        {

            lock (_sync)
            {

                RemoveClosure(acquiredClosure);

            }

            return new Error(
                ErrorCodes.Covenant.MaintenanceFailed,
                "Covenant leases over this scope did not drain within the operation bound, so nothing was changed.");

        }

        lock (_sync)
        {

            ExclusiveRegistration registration = new(
                this,
                acquiredClosure,
                BuildExclusiveSnapshot(acquiredClosure, acquisitionFacts.Value));

            acquiredClosure.LiveRegistration = registration;

            acquiredClosure.AcquisitionInProgress = false;

            return new CovenantExclusiveLease(registration);

        }

    }

    private static CovenantOperationLeaseSnapshot BuildExclusiveSnapshot(Closure closure, GateFacts facts) =>
        new(
            RegistrationId: Guid.NewGuid(),
            RuntimeAuthorityGeneration: facts.RuntimeAuthorityGeneration,
            Kind: closure.LeaseKind,
            Coverage: closure.Scope is null ? CovenantLeaseCoverage.Installation : CovenantLeaseCoverage.Scoped,
            Scope: closure.Scope,
            DatasetGeneration: facts.DatasetGeneration,
            CapabilityGeneration: facts.CapabilityGeneration,
            AuthorityEpoch: facts.AuthorityEpoch,
            CanonicalSequence: facts.CanonicalSequence,
            CampaignAvailabilityGeneration: null,
            CampaignPathRevision: null,
            AcceleratorEpoch: facts.AcceleratorEpoch,
            AppliedCampaignDeletionSequence: facts.AppliedCampaignDeletionSequence,
            RecoveryOwner: closure.Owner,
            CleanupOnlyHistoricalCampaign: closure.CleanupOnlyHistoricalCampaign);

    private async Task<bool> DrainAsync(
        List<ScopedRegistration> draining,
        CancellationToken cancellationToken)
    {

        if (draining.Count == 0)
        {

            return true;

        }

        try
        {

            await Task.WhenAll(draining.Select(static registration => registration.Released))
                .WaitAsync(_drainTimeout, cancellationToken)
                .ConfigureAwait(false);

            return true;

        }
        catch (TimeoutException)
        {

            return false;

        }
        catch (OperationCanceledException)
        {

            return false;

        }

    }

    private bool IsAcquisitionBlocked(CovenantLeaseCoverage coverage, CovenantOperationScope? scope)
    {

        if (_installationClosure is not null)
        {

            return true;

        }

        if (coverage == CovenantLeaseCoverage.Installation)
        {

            // Installation coverage spans every scope, so any pending close excludes it.
            return _globalScopeClosure is not null || _campaignClosures.Count > 0;

        }

        return scope!.Value.Kind == CovenantScope.Global
            ? _globalScopeClosure is not null
            : _campaignClosures.ContainsKey(scope.Value.CampaignId!.Value);

    }

    private bool TryInstallClosure(Closure closure)
    {

        switch (closure.Slot)
        {

            case ClosureSlot.Installation:

                if (_installationClosure is not null
                    || _globalScopeClosure is not null
                    || _campaignClosures.Count > 0)
                {

                    return false;

                }

                _installationClosure = closure;

                return true;

            case ClosureSlot.GlobalScope:

                if (_installationClosure is not null || _globalScopeClosure is not null)
                {

                    return false;

                }

                _globalScopeClosure = closure;

                return true;

            default:

                Guid campaignId = closure.Scope!.Value.CampaignId!.Value;

                if (_installationClosure is not null || _campaignClosures.ContainsKey(campaignId))
                {

                    return false;

                }

                _campaignClosures.Add(campaignId, closure);

                return true;

        }

    }

    private void RemoveClosure(Closure closure)
    {

        switch (closure.Slot)
        {

            case ClosureSlot.Installation:

                if (ReferenceEquals(_installationClosure, closure))
                {

                    _installationClosure = null;

                }

                break;

            case ClosureSlot.GlobalScope:

                if (ReferenceEquals(_globalScopeClosure, closure))
                {

                    _globalScopeClosure = null;

                }

                break;

            default:

                Guid campaignId = closure.Scope!.Value.CampaignId!.Value;

                if (_campaignClosures.TryGetValue(campaignId, out Closure? existing)
                    && ReferenceEquals(existing, closure))
                {

                    _ = _campaignClosures.Remove(campaignId);

                }

                break;

        }

    }

    private Closure? FindClosure(ClosureSlot slot, Guid? campaignId) =>
        slot switch
        {
            ClosureSlot.Installation => _installationClosure,

            ClosureSlot.GlobalScope => _globalScopeClosure,

            _ => campaignId is { } id && _campaignClosures.TryGetValue(id, out Closure? closure)
                ? closure
                : null,
        };

    private Result<GateFacts> CaptureFacts(
        bool requireCanonical,
        CovenantExclusiveRecoveryOwner? recoveryOwner = null)
    {

        CovenantRuntimeGenerationState state = _runtime.Current;

        CovenantAuthoritySnapshot? authority = state.ActiveAuthority;

        if (authority is null
            && recoveryOwner is { } owner
            && state.AuthorityRetired
            && state.RecoveryOwner == owner)
        {

            authority = state.AuthoritySlot;

        }

        if (authority is null)
        {

            return new Error(
                ErrorCodes.Covenant.OperatorAuthorityUnavailable,
                "Installation authority has not been established, so no Covenant lease can be bound to it.");

        }

        CovenantAvailabilitySnapshot availability = state.Availability;

        if (requireCanonical
            && (availability.Canonical != CovenantCapabilityState.Healthy
                || availability.DatasetGeneration is null))
        {

            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "The Covenant canonical tier is not healthy, so no ordinary lease can be taken over it.");

        }

        return new GateFacts(
            availability.DatasetGeneration,
            availability.Generation,
            authority.RuntimeAuthorityGeneration,
            authority.AuthorityEpoch,
            availability.CanonicalSequence,
            availability.AcceleratorEpoch,
            availability.AppliedCampaignDeletionSequence);

    }

    private Result Revalidate(CovenantOperationLeaseSnapshot snapshot, bool exclusive)
    {

        CovenantRuntimeGenerationState state = _runtime.Current;

        CovenantAuthoritySnapshot? authority = state.ActiveAuthority;

        if (exclusive
            && authority is null
            && snapshot.RecoveryOwner is { } owner
            && state.AuthorityRetired
            && state.RecoveryOwner == owner)
        {

            authority = state.AuthoritySlot;

        }

        if (authority is null
            || authority.RuntimeAuthorityGeneration != snapshot.RuntimeAuthorityGeneration
            || authority.AuthorityEpoch != snapshot.AuthorityEpoch)
        {

            return new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "Installation authority changed after this Covenant lease was taken.");

        }

        CovenantAvailabilitySnapshot availability = state.Availability;

        if (snapshot.DatasetGeneration is { } captured && availability.DatasetGeneration != captured)
        {

            return new Error(
                ErrorCodes.Covenant.StaleSnapshot,
                "The Covenant dataset generation changed after this lease was taken.");

        }

        if (snapshot.AcceleratorEpoch is { } acceleratorEpoch
            && availability.AcceleratorEpoch != acceleratorEpoch)
        {

            return new Error(
                ErrorCodes.Covenant.StaleSnapshot,
                "The Covenant accelerator epoch changed after this lease was taken.");

        }

        if (!exclusive && availability.Canonical != CovenantCapabilityState.Healthy)
        {

            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "The Covenant canonical tier stopped being healthy while this lease was held.");

        }

        return Result.Success();

    }

    private void Release(ScopedRegistration registration)
    {

        lock (_sync)
        {

            // Compare the exact instance: a delayed cleanup must never evict a registration that has
            // since reused this identity.
            if (_registrations.TryGetValue(registration.Snapshot.RegistrationId, out ScopedRegistration? live)
                && ReferenceEquals(live, registration))
            {

                _ = _registrations.Remove(registration.Snapshot.RegistrationId);

            }

        }

    }

    private void ReleaseExclusive(ExclusiveRegistration registration)
    {

        lock (_sync)
        {

            if (ReferenceEquals(registration.Closure.LiveRegistration, registration))
            {

                registration.Closure.LiveRegistration = null;

            }

        }

    }

    private Result CompleteExclusive(
        ExclusiveRegistration registration,
        CovenantExclusiveLeaseDisposition disposition)
    {

        lock (_sync)
        {

            if (!ReferenceEquals(registration.Closure.LiveRegistration, registration))
            {

                return new Error(
                    ErrorCodes.Covenant.LifecycleConflict,
                    "This exclusive Covenant lease no longer owns its closed scope.");

            }

            if (disposition != CovenantExclusiveLeaseDisposition.KeepClosed)
            {

                RemoveClosure(registration.Closure);

                registration.Closure.LiveRegistration = null;

            }

            return Result.Success();

        }

    }

    private Result ExecuteWhileHeld(
        ExclusiveRegistration registration,
        Func<Result> callback)
    {

        lock (_sync)
        {

            Closure closure = registration.Closure;

            if (!ReferenceEquals(FindClosure(closure.Slot, closure.Scope?.CampaignId), closure)
                || !ReferenceEquals(closure.LiveRegistration, registration)
                || registration.IsReleased
                || registration.IsDispositionClaimed)
            {

                return new Error(
                    ErrorCodes.Covenant.LifecycleConflict,
                    "This exclusive Covenant lease no longer owns its exact closed scope.");

            }

            return callback();

        }

    }

    private Result RevalidateExclusive(ExclusiveRegistration registration)
    {

        lock (_sync)
        {

            Closure closure = registration.Closure;

            if (!ReferenceEquals(FindClosure(closure.Slot, closure.Scope?.CampaignId), closure)
                || !ReferenceEquals(closure.LiveRegistration, registration)
                || registration.IsReleased
                || registration.IsDispositionClaimed)
            {

                return new Error(
                    ErrorCodes.Covenant.StaleSnapshot,
                    "This exclusive Covenant lease no longer owns its exact closed scope.");

            }

        }

        return Revalidate(registration.Snapshot, exclusive: true);

    }

    private enum ClosureSlot : byte
    {

        Installation = 1,

        GlobalScope = 2,

        Campaign = 3,

    }

    private readonly record struct GateFacts(
        Guid? DatasetGeneration,
        long CapabilityGeneration,
        long RuntimeAuthorityGeneration,
        long AuthorityEpoch,
        long CanonicalSequence,
        ulong AcceleratorEpoch,
        long? AppliedCampaignDeletionSequence);

    /// <summary>
    /// One closed scope and the exact operation that closed it.
    /// </summary>
    private sealed class Closure(
        CovenantExclusiveRecoveryOwner owner,
        ClosureSlot slot,
        CovenantOperationScope? scope,
        CovenantLeaseKind leaseKind)
    {

        public CovenantExclusiveRecoveryOwner Owner { get; } = owner;

        public ClosureSlot Slot { get; } = slot;

        public CovenantOperationScope? Scope { get; } = scope;

        public CovenantLeaseKind LeaseKind { get; } = leaseKind;

        public bool CleanupOnlyHistoricalCampaign { get; init; }

        public bool AcquisitionInProgress { get; set; }

        /// <summary>
        /// The live recovery lease, or null while the scope is kept closed without one.
        /// </summary>
        public ExclusiveRegistration? LiveRegistration { get; set; }

        public bool Covers(ScopedRegistration registration) =>
            Slot switch
            {
                ClosureSlot.Installation => true,

                ClosureSlot.GlobalScope =>
                    registration.Snapshot.Coverage == CovenantLeaseCoverage.Installation
                    || registration.Snapshot.Scope!.Value.Kind == CovenantScope.Global,

                _ => registration.Snapshot.Coverage == CovenantLeaseCoverage.Installation
                    || registration.Snapshot.Scope!.Value.CampaignId == Scope!.Value.CampaignId,
            };

    }

    private sealed class ScopedRegistration : ICovenantLeaseRegistration, IDisposable
    {

        private readonly CovenantOperationGate _gate;

        private readonly CancellationTokenSource _revocation = new();

        private readonly TaskCompletionSource _released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _releaseClaimed;

        internal ScopedRegistration(CovenantOperationGate gate, CovenantOperationLeaseSnapshot snapshot)
        {

            _gate = gate;

            Snapshot = snapshot;

        }

        public CovenantOperationLeaseSnapshot Snapshot { get; }

        public CancellationToken Revocation => _revocation.Token;

        internal Task Released => _released.Task;

        internal void RequestRevocation()
        {

            try
            {

                if (!_revocation.IsCancellationRequested)
                {

                    _revocation.Cancel();

                }

            }
            catch (ObjectDisposedException)
            {

                // The holder released between the drain set being snapshotted and this cancellation.
                // That is the outcome the revocation was asking for, so there is nothing left to do.

            }

        }

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            if (Volatile.Read(ref _releaseClaimed) != 0)
            {

                return ValueTask.FromResult(
                    Result.Failure(
                        new Error(ErrorCodes.Covenant.StaleSnapshot, "This Covenant lease has already been released.")));

            }

            if (_revocation.IsCancellationRequested)
            {

                return ValueTask.FromResult(
                    Result.Failure(
                        new Error(
                            ErrorCodes.Covenant.Unavailable,
                            "A Covenant operation revoked this lease so its scope could be closed.")));

            }

            return ValueTask.FromResult(_gate.Revalidate(Snapshot, exclusive: false));

        }

        public ValueTask ReleaseAsync()
        {

            if (Interlocked.Exchange(ref _releaseClaimed, 1) == 0)
            {

                _gate.Release(this);

                Dispose();

                _ = _released.TrySetResult();

            }

            return ValueTask.CompletedTask;

        }

        public void Dispose() => _revocation.Dispose();

    }

    private sealed class ExclusiveRegistration(
        CovenantOperationGate gate,
        Closure closure,
        CovenantOperationLeaseSnapshot snapshot) : ICovenantExclusiveLeaseRegistration
    {

        private int _dispositionClaimed;

        private int _releaseClaimed;

        internal Closure Closure { get; } = closure;

        public CovenantOperationLeaseSnapshot Snapshot { get; } = snapshot;

        /// <summary>
        /// An exclusive lease is never revoked: it is the operation everything else was drained for.
        /// </summary>
        public CancellationToken Revocation => CancellationToken.None;

        internal bool IsDispositionClaimed => Volatile.Read(ref _dispositionClaimed) != 0;

        internal bool IsReleased => Volatile.Read(ref _releaseClaimed) != 0;

        public Result ExecuteWhileHeld(Func<Result> callback)
        {

            ArgumentNullException.ThrowIfNull(callback);

            return gate.ExecuteWhileHeld(this, callback);

        }

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult(gate.RevalidateExclusive(this));

        }

        public ValueTask<Result> CompleteAsync(
            CovenantExclusiveLeaseDisposition disposition,
            CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            if (Interlocked.Exchange(ref _dispositionClaimed, 1) != 0)
            {

                return ValueTask.FromResult(
                    Result.Failure(
                        new Error(
                            ErrorCodes.Covenant.LifecycleConflict,
                            "This exclusive Covenant registration has already used its disposition.")));

            }

            return ValueTask.FromResult(gate.CompleteExclusive(this, disposition));

        }

        public ValueTask ReleaseAsync()
        {

            if (Interlocked.Exchange(ref _releaseClaimed, 1) == 0)
            {

                gate.ReleaseExclusive(this);

            }

            return ValueTask.CompletedTask;

        }

    }

}
