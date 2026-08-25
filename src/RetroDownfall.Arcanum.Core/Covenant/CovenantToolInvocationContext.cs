using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// The non-serializable, single-use capability one Covenant MCP request runs under.
/// </summary>
/// <remarks>
/// The in-process MCP server executes on its own task, so a handler cannot inherit the turn's async
/// flow and an <c>AsyncLocal</c> alone would either leak across concurrent requests or vanish. The
/// capability is therefore an explicit object the API pipeline mints per tool call, registers under
/// the exact connection-and-request id, and hands over exactly once. Everything a Covenant mutation
/// needs — the turn's collector, its canonical Campaign, the admission that produced this call, the
/// call-scoped materialization snapshot, and the retirement preflight and Ward receipt when one is
/// required — travels inside it, so a handler has nothing to re-derive and no way to widen its own
/// authority (§10.14).
///
/// <para>Taking is one-shot and disposal is a drain rather than a flag. A use that suspends across an
/// <c>await</c> and resumes after the turn ended finds <see cref="CovenantToolCapabilityState.Closing"/>
/// or <see cref="CovenantToolCapabilityState.Disposed"/> and stops; a disposal that returned while a
/// lease was still open would let exactly that continuation stage into a finished turn.</para>
/// </remarks>
public sealed class CovenantToolInvocationContext : IAsyncDisposable
{

    private readonly Lock _gate = new();

    private readonly ICovenantMutationCollector _collector;

    private readonly CovenantToolCapabilityNonce _nonce;

    private readonly ICovenantTurnHeadProbe _headProbe;

    private readonly CancellationTokenSource _closing;

    private int _outstandingUses;

    private TaskCompletionSource? _drained;

    public CovenantToolInvocationContext(
        ICovenantMutationCollector collector,
        CanonicalCampaignContext campaign,
        CovenantAdmissionReceipt producingAdmission,
        ProviderCallMaterializationSnapshot callMaterialization,
        ICovenantTurnHeadProbe headProbe,
        CovenantToolCapabilityNonce nonce,
        string toolName,
        string toolCallId,
        CovenantRetirementPreflight? retirementPreflight,
        CovenantToolWardReceipt? wardReceipt,
        CancellationToken turnCancellation)
    {

        ArgumentNullException.ThrowIfNull(collector);

        ArgumentNullException.ThrowIfNull(producingAdmission);

        ArgumentNullException.ThrowIfNull(callMaterialization);

        ArgumentNullException.ThrowIfNull(headProbe);

        ArgumentException.ThrowIfNullOrEmpty(toolCallId);

        if (!CovenantToolNames.IsCovenantMutationTool(toolName))
        {

            throw new ArgumentException(
                "A Covenant tool capability authorizes exactly one of the two Covenant mutation tools.",
                nameof(toolName));

        }

        if (!nonce.IsValid)
        {

            throw new ArgumentException(
                "A Covenant tool capability requires a minted capability nonce.",
                nameof(nonce));

        }

        if (!campaign.IsCampaignBound)
        {

            // The Proposed lane is Campaign-only and retirement is Campaign-only, so a Global-only
            // turn has no scope an agent could ever write into.
            throw new ArgumentException(
                "A Covenant tool capability requires a canonical Campaign binding.",
                nameof(campaign));

        }

        if (producingAdmission.Plan.Digest != collector.BasePlanDigest)
        {

            throw new ArgumentException(
                "A Covenant tool capability must bind the turn plan its admission was derived from.",
                nameof(producingAdmission));

        }

        bool isRetirement = string.Equals(toolName, CovenantToolNames.RetireCovenant, StringComparison.Ordinal);

        if (isRetirement && (retirementPreflight is null || wardReceipt is null))
        {

            throw new ArgumentException(
                "A retirement capability carries both its resolved target preflight and its Ward receipt.",
                nameof(retirementPreflight));

        }

        if (!isRetirement && (retirementPreflight is not null || wardReceipt is not null))
        {

            throw new ArgumentException(
                "A proposal capability carries no retirement target and no Ward receipt.",
                nameof(retirementPreflight));

        }

        _collector = collector;

        _nonce = nonce;

        _headProbe = headProbe;

        Campaign = campaign;

        ProducingAdmission = producingAdmission;

        CallMaterialization = callMaterialization;

        ToolName = toolName;

        ToolCallId = toolCallId;

        RetirementPreflight = retirementPreflight;

        WardReceipt = wardReceipt;

        LogicalTurnId = collector.LogicalTurnId;

        BasePlanDigest = collector.BasePlanDigest;

        BranchId = producingAdmission.BranchId;

        MutationId = Guid.CreateVersion7();

        _closing = CancellationTokenSource.CreateLinkedTokenSource(turnCancellation);

    }

    /// <summary>
    /// The mutation identity this call will stage under.
    /// </summary>
    /// <remarks>
    /// One capability is one tool call, so its mutation id is minted here rather than by the handler.
    /// A handler that generated its own could produce two ids for one call after an internal retry,
    /// and the durable receipt ledger keys on this value.
    /// </remarks>
    public Guid MutationId { get; }

    public CovenantToolCapabilityState State { get; private set; } = CovenantToolCapabilityState.Registered;

    public CanonicalCampaignContext Campaign { get; }

    public CovenantAdmissionReceipt ProducingAdmission { get; }

    /// <summary>
    /// The exact materialization snapshot of the provider call that emitted this tool request.
    /// </summary>
    /// <remarks>
    /// This, and never the turn-cumulative attachment gate, is a proposal's provenance. The
    /// cumulative gate would let content materialized for an earlier call authorize a later
    /// proposal that never saw it.
    /// </remarks>
    public ProviderCallMaterializationSnapshot CallMaterialization { get; }

    public string ToolName { get; }

    public string ToolCallId { get; }

    public CovenantRetirementPreflight? RetirementPreflight { get; }

    public CovenantToolWardReceipt? WardReceipt { get; }

    public Guid LogicalTurnId { get; }

    public CovenantDigest BasePlanDigest { get; }

    /// <summary>The provider branch this capability was admitted on.</summary>
    public Guid BranchId { get; }

    /// <summary>The turn's staging collector, reachable only while the capability is live.</summary>
    public Result<ICovenantMutationCollector> ResolveCollector(CovenantToolCapabilityNonce nonce)
    {

        lock (_gate)
        {

            Result usable = CheckUsable(nonce);

            return usable.IsFailure
                ? Result<ICovenantMutationCollector>.Failure(usable.Error)
                : Result<ICovenantMutationCollector>.Success(_collector);

        }

    }

    /// <summary>
    /// The one bounded indexed head probe this turn is allowed to make for an unplanned key.
    /// </summary>
    /// <remarks>
    /// A proposal for a key that the turn plan never materialized still needs an expected revision,
    /// and it needs to tell a never-created lane from a tombstone. The probe is reached through the
    /// capability rather than resolved by the handler so that a call which is no longer live cannot
    /// open a read at all.
    /// </remarks>
    public async ValueTask<Result<CovenantLaneHeadProbe>> ProbeLaneHeadAsync(
        CovenantToolCapabilityNonce nonce,
        CovenantLane lane,
        string normalizedKey,
        CancellationToken cancellationToken)
    {

        Result<IDisposable> lease = TryAcquireUse(nonce);

        if (lease.IsFailure)
        {
            return Result<CovenantLaneHeadProbe>.Failure(lease.Error);
        }

        using IDisposable use = lease.Value;

        Result usable = RecheckBeforeIrreversibleEffect(nonce);

        if (usable.IsFailure)
        {
            return Result<CovenantLaneHeadProbe>.Failure(usable.Error);
        }

        return await _headProbe
            .ProbeAsync(lane, normalizedKey, cancellationToken)
            .ConfigureAwait(false);

    }

    /// <summary>
    /// Measures the Section a staged mutation would join, before anything is staged into it.
    /// </summary>
    /// <remarks>
    /// Reached through the capability for the same reason the head probe is: a call that is no longer
    /// live must not be able to open a read at all. What it answers is the fact the staging decision
    /// turns on — a proposal admitted into a full Section is refused by the write authority, and that
    /// authority runs inside the transaction carrying the operator's answer, so the turn loses the
    /// reply rather than only the proposal.
    /// </remarks>
    public async ValueTask<Result<CovenantSectionOccupancy>> ProbeSectionAsync(
        CovenantToolCapabilityNonce nonce,
        CovenantLane lane,
        ImmutableArray<string> excludedKeys,
        CancellationToken cancellationToken)
    {

        Result<IDisposable> lease = TryAcquireUse(nonce);

        if (lease.IsFailure)
        {
            return Result<CovenantSectionOccupancy>.Failure(lease.Error);
        }

        using IDisposable use = lease.Value;

        Result usable = RecheckBeforeIrreversibleEffect(nonce);

        if (usable.IsFailure)
        {
            return Result<CovenantSectionOccupancy>.Failure(usable.Error);
        }

        return await _headProbe
            .ProbeSectionAsync(lane, excludedKeys, cancellationToken)
            .ConfigureAwait(false);

    }

    /// <summary>
    /// Transitions <see cref="CovenantToolCapabilityState.Registered"/> to
    /// <see cref="CovenantToolCapabilityState.Taken"/> exactly once.
    /// </summary>
    public Result TryTake(CovenantToolCapabilityNonce nonce)
    {

        lock (_gate)
        {

            if (!_nonce.Equals(nonce))
            {
                return Result.Failure(NonceError());
            }

            if (State != CovenantToolCapabilityState.Registered)
            {
                return Result.Failure(StateError());
            }

            State = CovenantToolCapabilityState.Taken;

            return Result.Success();

        }

    }

    /// <summary>Takes the short atomic lease every capability operation runs under.</summary>
    public Result<IDisposable> TryAcquireUse(CovenantToolCapabilityNonce nonce)
    {

        lock (_gate)
        {

            Result usable = CheckUsable(nonce);

            if (usable.IsFailure)
            {
                return Result<IDisposable>.Failure(usable.Error);
            }

            _outstandingUses++;

            return Result<IDisposable>.Success(new UseLease(this));

        }

    }

    /// <summary>
    /// Revalidates every fact this capability depends on, immediately before an irreversible effect.
    /// </summary>
    /// <remarks>
    /// Called after every <c>await</c> that precedes a disclosure or a staging action, never once at
    /// handler entry. Between those two points the turn can be cancelled, the collector can seal, and
    /// a provider fallback can abandon the branch this call was admitted on — and each of those makes
    /// the effect wrong in a different way.
    /// </remarks>
    public Result RecheckBeforeIrreversibleEffect(CovenantToolCapabilityNonce nonce)
    {

        lock (_gate)
        {

            Result usable = CheckUsable(nonce);

            if (usable.IsFailure)
            {
                return usable;
            }

            if (_collector.State != CovenantCollectorState.Open)
            {
                return Result.Failure(new Error(
                    ErrorCodes.Covenant.StaleSnapshot,
                    "This turn's Covenant collector no longer accepts staged mutations."));
            }

            if (_collector.CurrentBranchId != BranchId)
            {
                return Result.Failure(new Error(
                    ErrorCodes.Covenant.StaleSnapshot,
                    "The provider branch this Covenant tool call was admitted on has been abandoned."));
            }

            return Result.Success();

        }

    }

    public async ValueTask DisposeAsync()
    {

        Task drain;

        lock (_gate)
        {

            if (State == CovenantToolCapabilityState.Disposed)
            {
                return;
            }

            if (State != CovenantToolCapabilityState.Closing)
            {
                State = CovenantToolCapabilityState.Closing;
            }

            if (_outstandingUses == 0)
            {
                Finish();

                return;
            }

            _drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            drain = _drained.Task;

        }

        // Outside the lock: a suspended use resuming here has to be able to observe Closing.
        await _closing.CancelAsync().ConfigureAwait(false);

        await drain.ConfigureAwait(false);

        lock (_gate)
        {

            if (State != CovenantToolCapabilityState.Disposed)
            {
                Finish();
            }

        }

    }

    private void Finish()
    {

        State = CovenantToolCapabilityState.Disposed;

        _closing.Dispose();

    }

    private Result CheckUsable(CovenantToolCapabilityNonce nonce)
    {

        if (!_nonce.Equals(nonce))
        {
            return Result.Failure(NonceError());
        }

        if (State != CovenantToolCapabilityState.Taken)
        {
            return Result.Failure(StateError());
        }

        if (_closing.IsCancellationRequested)
        {
            return Result.Failure(new Error(
                ErrorCodes.Covenant.LifecycleConflict,
                "This Covenant tool capability was cancelled with its turn."));
        }

        return Result.Success();

    }

    private static Error NonceError() =>
        new(
            ErrorCodes.Covenant.ForbiddenAuthority,
            "This Covenant tool capability was presented with the wrong capability nonce.");

    private Error StateError() =>
        new(
            ErrorCodes.Covenant.LifecycleConflict,
            State switch
            {
                CovenantToolCapabilityState.Registered =>
                    "This Covenant tool capability has not been taken by its handler.",
                CovenantToolCapabilityState.Taken =>
                    "This Covenant tool capability has already been taken.",
                CovenantToolCapabilityState.Closing =>
                    "This Covenant tool capability is closing.",
                _ => "This Covenant tool capability was disposed.",
            });

    private void ReleaseUse()
    {

        TaskCompletionSource? drained = null;

        lock (_gate)
        {

            if (_outstandingUses > 0)
            {
                _outstandingUses--;
            }

            if (_outstandingUses == 0
                && State == CovenantToolCapabilityState.Closing
                && _drained is not null)
            {
                drained = _drained;
            }

        }

        _ = drained?.TrySetResult();

    }

    private sealed class UseLease(CovenantToolInvocationContext owner) : IDisposable
    {

        private bool _released;

        public void Dispose()
        {

            if (_released)
            {
                return;
            }

            _released = true;

            owner.ReleaseUse();

        }

    }

}
