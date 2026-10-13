using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Core.LongRest;

/// <summary>
/// Borrowed authenticated request authority, issued only by API composition after durable claim
/// validation. Never serialize or persist it. The caller retains and owns the registered turn lease.
/// </summary>
public sealed class LongRestMaintenanceCapability
{
    private readonly object _consumptionLock = new();

    private readonly CancellationToken _revocation;

    private readonly TimeProvider _timeProvider;

    private SessionTurnClaimLease _claim;

    private int _consumed;

    private LongRestSweepScope _operationScope;

    private LongRestMaintenanceCapability(Guid operationId, ArcanumInvocationContext invocation, SessionTurnClaimLease claim, CanonicalCampaignContext scope, CovenantTurnLease authority, LongRestConfigurationStamp configuration, CancellationToken revocation, TimeProvider timeProvider)
    {
        OperationId = operationId;

        Invocation = invocation;

        _claim = claim;

        CanonicalScope = scope;

        Authority = authority;

        Configuration = configuration;

        _revocation = revocation;

        _timeProvider = timeProvider;
    }

    public Guid OperationId { get; }

    public ArcanumInvocationContext Invocation { get; }

    public SessionTurnClaimLease Claim
    {
        get
        {
            lock (_consumptionLock)
            {
                return _claim;
            }
        }
    }

    public CanonicalCampaignContext CanonicalScope { get; }

    public CovenantTurnLease Authority { get; }

    public LongRestConfigurationStamp Configuration { get; }

    public CancellationToken Revocation => _revocation;

    internal static async Task<Result<LongRestMaintenanceCapability>> CreateAsync(Guid operationId, ArcanumInvocationContext invocation, SessionTurnClaimLease claim, CanonicalCampaignContext scope, CovenantTurnLease authority, LongRestConfigurationStamp configuration, CancellationToken ct, TimeProvider? timeProvider = null)
    {
        ct.ThrowIfCancellationRequested();

        if (operationId == Guid.Empty || invocation is null || claim is null || authority is null || configuration is not { IsValid: true })
        {
            return Forbidden("This request lacks exact Long Rest operation authority.");
        }

        Result registered = await authority.RevalidateAsync(ct).ConfigureAwait(false);

        if (registered.IsFailure)
        {
            return Result<LongRestMaintenanceCapability>.Failure(registered.Error);
        }

        CancellationToken revocation;

        try
        {
            revocation = authority.Revocation;
        }
        catch (ObjectDisposedException)
        {
            return Forbidden("The borrowed turn lease has already been released.");
        }

        LongRestMaintenanceCapability capability = new(operationId, invocation, claim, scope, authority, configuration, revocation, timeProvider ?? TimeProvider.System);

        Result live = await capability.ValidateAsync(configuration, ct).ConfigureAwait(false);

        return live.IsSuccess ? capability : Result<LongRestMaintenanceCapability>.Failure(live.Error);
    }

    /// <summary>Accepts only the exact persisted renewal returned to trusted API composition.</summary>
    internal async Task<Result> RefreshClaimAsync(SessionTurnClaimLease renewed, LongRestConfigurationStamp configuration, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        Result registered = await Authority.RevalidateAsync(ct).ConfigureAwait(false);

        if (registered.IsFailure)
        {
            return registered;
        }

        lock (_consumptionLock)
        {
            SessionTurnClaimLease current = _claim;

            DateTimeOffset now = _timeProvider.GetUtcNow();

            if (renewed is null || configuration != Configuration || Revocation.IsCancellationRequested
                || current.LeaseDeadlineUtc is not { } currentDeadline || currentDeadline <= now
                || renewed.LeaseDeadlineUtc is not { } renewedDeadline || renewedDeadline < currentDeadline
                || renewed.Disposition is < SessionTurnClaimDisposition.Created or > SessionTurnClaimDisposition.Adopted
                || renewed.ExecutorId != current.ExecutorId || renewed.OwnerBootId != current.OwnerBootId
                || renewed.FutureAssistantEntryId != current.FutureAssistantEntryId
                || renewed.Claim.State != SessionTurnClaimState.PendingMaintenance
                || renewed.Claim.CheckpointRevision < current.Claim.CheckpointRevision
                || renewed.Claim.ExpectedCurrentSensitivityRevision < current.Claim.ExpectedCurrentSensitivityRevision
                || (renewed.Claim.CompletedStepMask & current.Claim.CompletedStepMask) != current.Claim.CompletedStepMask
                || renewed.Claim with
                {
                    CheckpointRevision = current.Claim.CheckpointRevision,

                    ExpectedCurrentSensitivityRevision = current.Claim.ExpectedCurrentSensitivityRevision,

                    CompletedStepMask = current.Claim.CompletedStepMask,
                } != current.Claim)
            {
                return Stale("A claim renewal cannot replace identity, regress committed facts or revive expired authority.");
            }

            _claim = renewed;
        }

        return Result.Success();
    }

    /// <summary>Consumes the root once; failed scope/configuration checks do not spend authority.</summary>
    public async ValueTask<Result> ConsumeAsync(Guid operationId, LongRestSweepScope scope, LongRestConfigurationStamp configuration, CancellationToken ct)
    {
        if (operationId != OperationId || !MatchesScope(scope))
        {
            return Stale("The Long Rest operation or exact scope does not match this request.");
        }

        Result live = await ValidateAsync(configuration, ct).ConfigureAwait(false);

        if (live.IsFailure)
        {
            return live;
        }

        lock (_consumptionLock)
        {
            if (_consumed != 0)
            {
                return Stale("This Long Rest maintenance capability has already been consumed.");
            }

            _operationScope = scope;

            Volatile.Write(ref _consumed, 1);
        }

        return Result.Success();
    }

    /// <summary>Derives one exact physical-send capability within the already consumed operation.</summary>
    public async Task<Result<LongRestDispatchCapability>> CreateDispatchAsync(LongRestWorkIdentity work, CovenantDigest payloadDigest, CovenantDigest destinationDigest, ulong physicalOrdinal, LongRestConfigurationStamp configuration, CancellationToken ct)
    {
        Result live = await ValidateWorkAsync(work, configuration, ct).ConfigureAwait(false);

        if (live.IsFailure)
        {
            return Result<LongRestDispatchCapability>.Failure(live.Error);
        }

        if (!payloadDigest.IsValid || !destinationDigest.IsValid || physicalOrdinal == 0)
        {
            return ForbiddenDispatch("A physical send requires exact payload, destination and nonzero ordinal.");
        }

        return LongRestDispatchCapability.Create(this, LongRestDiscoveryHash.Work(work).Value, payloadDigest, destinationDigest, physicalOrdinal);
    }

    /// <summary>Rechecks borrowed request authority; API/store seams also revalidate the live claim.</summary>
    public async ValueTask<Result> ValidateAsync(LongRestConfigurationStamp configuration, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        Result registered = await Authority.RevalidateAsync(ct).ConfigureAwait(false);

        if (registered.IsFailure)
        {
            return registered;
        }

        CovenantOperationLeaseSnapshot snapshot = Authority.Snapshot;

        bool canonicalMatches;

        try
        {
            canonicalMatches = snapshot.Scope == (CanonicalScope.IsCampaignBound
                ? CovenantOperationScope.ForCampaign(CanonicalScope.CampaignId!.Value)
                : CovenantOperationScope.Global)
                && snapshot.CampaignAvailabilityGeneration == CanonicalScope.CampaignAvailabilityGeneration
                && snapshot.CampaignPathRevision == CanonicalScope.PathIdentityRevision;
        }
        catch (InvalidOperationException)
        {
            canonicalMatches = false;
        }

        lock (_consumptionLock)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();

            SessionTurnClaimLease claim = _claim;

            if (configuration != Configuration
                || Invocation.Surface != ArcanumExecutionSurface.SessionBackedOperatorTurn
                || !Invocation.CanReadCovenant || Invocation.Campaign != CanonicalScope
                || !Invocation.ReadAuthorityEpoch!.Matches(snapshot)
                || snapshot.Kind != CovenantLeaseKind.Turn || snapshot.Coverage != CovenantLeaseCoverage.Scoped
                || !canonicalMatches || Revocation.IsCancellationRequested
                || claim.Disposition is < SessionTurnClaimDisposition.Created or > SessionTurnClaimDisposition.Adopted
                || claim.Claim.State != SessionTurnClaimState.PendingMaintenance
                || claim.ExecutorId is null || claim.ExecutorId == Guid.Empty
                || claim.OwnerBootId != Configuration.BootId || claim.Claim.OwnerBootId != claim.OwnerBootId
                || claim.LeaseDeadlineUtc is not { } deadline || deadline <= now)
            {
                return Stale("The live authenticated request no longer covers this exact Long Rest work.");
            }
        }

        return Result.Success();
    }

    /// <summary>Verifies exact consumed operation authority before a store materializes protected work.</summary>
    public async ValueTask<Result> ValidateWorkAsync(LongRestWorkIdentity work, LongRestConfigurationStamp configuration, CancellationToken ct)
    {
        Result<CovenantDigest> identity = LongRestDiscoveryHash.Work(work);

        if (identity.IsFailure || Volatile.Read(ref _consumed) == 0
            || work.OperationId != OperationId || work.Scope != _operationScope
            || work.Fence.CanonicalScope != CanonicalScope || work.Fence.Configuration != Configuration
            || work.Fence.DatasetGeneration != Authority.Snapshot.DatasetGeneration
            || work.Fence.RuntimeAuthorityGeneration != Authority.Snapshot.RuntimeAuthorityGeneration
            || work.Fence.CapabilityGeneration != Authority.Snapshot.CapabilityGeneration)
        {
            return Stale("A dispatch requires the active operation's exact source and authority fence.");
        }

        return await ValidateAsync(configuration, ct).ConfigureAwait(false);
    }

    private bool MatchesScope(LongRestSweepScope scope)
    {
        if (!scope.IsInitialized)
        {
            return false;
        }

        try
        {
            return scope.CampaignId == CanonicalScope.CampaignId
                && (scope.Kind == LongRestSweepScopeKind.Campaign) == CanonicalScope.IsCampaignBound;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static Result<LongRestMaintenanceCapability> Forbidden(string message) => new Error(ErrorCodes.Covenant.ForbiddenAuthority, message);

    private static Result<LongRestDispatchCapability> ForbiddenDispatch(string message) => new Error(ErrorCodes.Covenant.ForbiddenAuthority, message);

    private static Result Stale(string message) => Result.Failure(new Error(ErrorCodes.Covenant.StaleSnapshot, message));
}

/// <summary>One exact physical send. Neither a retry nor another payload may reuse this child.</summary>
public sealed class LongRestDispatchCapability
{
    private readonly CovenantDigest _workDigest;

    private readonly CovenantDigest _payloadDigest;

    private readonly CovenantDigest _destinationDigest;

    private readonly ulong _physicalOrdinal;

    private int _consumed;

    private LongRestDispatchCapability(LongRestMaintenanceCapability root, CovenantDigest workDigest, CovenantDigest payloadDigest, CovenantDigest destinationDigest, ulong physicalOrdinal)
    {
        Root = root;

        _workDigest = workDigest;

        _payloadDigest = payloadDigest;

        _destinationDigest = destinationDigest;

        _physicalOrdinal = physicalOrdinal;
    }

    public LongRestMaintenanceCapability Root { get; }

    internal static LongRestDispatchCapability Create(LongRestMaintenanceCapability root, CovenantDigest workDigest, CovenantDigest payloadDigest, CovenantDigest destinationDigest, ulong physicalOrdinal) =>
        new(root, workDigest, payloadDigest, destinationDigest, physicalOrdinal);

    public async ValueTask<Result> ConsumeAsync(LongRestWorkIdentity work, CovenantDigest payloadDigest, CovenantDigest destinationDigest, ulong physicalOrdinal, LongRestConfigurationStamp configuration, CancellationToken ct)
    {
        Result<CovenantDigest> identity = LongRestDiscoveryHash.Work(work);

        if (identity.IsFailure || identity.Value != _workDigest || payloadDigest != _payloadDigest
            || destinationDigest != _destinationDigest || physicalOrdinal != _physicalOrdinal)
        {
            return Result.Failure(new Error(ErrorCodes.Covenant.StaleSnapshot, "This child capability names a different physical send."));
        }

        Result live = await Root.ValidateWorkAsync(work, configuration, ct).ConfigureAwait(false);

        if (live.IsFailure)
        {
            return live;
        }

        return Interlocked.Exchange(ref _consumed, 1) == 0
            ? Result.Success()
            : Result.Failure(new Error(ErrorCodes.Covenant.StaleSnapshot, "This Long Rest dispatch capability has already been consumed."));
    }
}
