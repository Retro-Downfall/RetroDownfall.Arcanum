using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Telemetry;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

namespace RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;

/// <summary>Serializes durable sweep progress under one acquired worker; request authority stays live.</summary>
internal sealed partial class LongRestSweepCoordinator(ILongRunningOperationStore operations, ILongRestSweepStore sweeps,
    ILongRestDiscoveryStore discovery, ILongRestConfigurationProvider configuration, LongRestLiveFenceReader facts,
    ILongRestAdmittedMaintenance maintenance, ICovenantOperationGate gate, LongRunningOperationOwnership ownership,
    LongRestSweepQueue queue, IServiceScopeFactory scopes, TimeProvider timeProvider,
    IGrimoireConnectionAdmissionGate admissionGate) : ILongRestSweepCoordinator, ILongRestAdmittedSweep
{
    internal const string AuthorizedRequestRequired = LongRestParkCodes.AuthorizedRequestRequired;

    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    public Task<Result<LongRunningOperationDto>> RequestAsync(CanonicalCampaignContext scope, CancellationToken ct)
    {
        try
        {
            LongRestSweepScope partition = Require(LongRestSweepScope.Create(new(
                scope.IsCampaignBound ? LongRestSweepScopeKind.Campaign : LongRestSweepScopeKind.Global, scope.CampaignId)));

            return RequestAsync(scope, partition, ct);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return Task.FromResult(Result<LongRunningOperationDto>.Failure(Unavailable()));
        }
    }

    public Task<Result<LongRunningOperationDto>> RequestAsync(CanonicalCampaignContext scope, LongRestSweepScope partition, CancellationToken ct) =>
        RequestCoreAsync(scope, partition, null, ct);

    public Task<Result<LongRunningOperationDto>> RequestAsync(CanonicalCampaignContext scope, LongRestSweepScope partition, CovenantTurnLease authority, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(authority);

        return RequestCoreAsync(scope, partition, authority, ct);
    }

    private async Task<Result<LongRunningOperationDto>> RequestCoreAsync(CanonicalCampaignContext scope, LongRestSweepScope partition, CovenantTurnLease? authority, CancellationToken ct)
    {
        try
        {
            LongRestSweepConfiguration options = CurrentOptions();

            if (!partition.IsInitialized || partition.CampaignId != scope.CampaignId
                || (partition.Kind == LongRestSweepScopeKind.Campaign) != scope.IsCampaignBound)
            {
                return Unavailable();
            }

            Require(await facts.CaptureAsync(scope, options.Stamp, null, ct).ConfigureAwait(false));

            if (sweeps is not ILongRestCompletedRequestReplay completedRequests) return Unavailable();

            Guid? completed = Require(await completedRequests.FindCompletedRequestReplayAsync(scope, partition, options.Recipe.Value, ct).ConfigureAwait(false));

            // A completed sweep answers a request only while it still describes the installation. One that no longer does, or that was
            // superseded, invalidated or purged since the lookup found it, is history: not an answer, and not a refusal either. The request
            // admits a new generation exactly as it does when no completed sweep matches (the old sweep keeps its evidence until its
            // successor completes). Only a live fence that cannot be captured at all is a refusal.
            if (completed is { } completedId
                && await ReplayCompletedAsync(completedId, scope, partition, options, ct).ConfigureAwait(false) is { } replay)
            {
                return replay;
            }

            LongRestSweepFence fence = Require(await facts.CaptureAsync(scope, options.Stamp, null, ct).ConfigureAwait(false));

            if (sweeps is not ILongRestSweepRequestAdmission requests) return Unavailable();

            // Only a protected partition can be freed by authority, and only a request that carries a turn lease has any to offer.
            Func<CancellationToken, ValueTask<Result>>? prove = authority is not null && partition.ProtectedPartitionDigest is not null
                ? token => ProveRequestAuthorityAsync(authority, scope, fence, options, token) : null;

            // The recipe lets admission look once more, inside its own transaction, for a sweep that completed while this request was between its
            // lookup and its admission: it then joins that sweep instead of paying for a second generation.
            LongRunningOperation admitted = Require(await requests.AdmitRequestAsync(partition, fence, facts, ownership, ct, prove, options.Recipe.Value).ConfigureAwait(false));

            if (partition.ProtectedPartitionDigest is null && admitted.State != LongRunningOperationState.Completed) queue.Signal(admitted.Id);

            return LongRunningOperationDto.FromOperation(admitted);
        }
        catch (Refused refusal)
        {
            return refusal.Error;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return Unavailable();
        }
    }

    /// <summary>
    /// The Session request path: it carries its own capability, so it mints its own Long Rest work lease, uses it for
    /// exactly this call and disposes it. The hosted worker never comes through here.
    /// </summary>
    public async Task<Result<LongRestSweepProgress>> ResumeAsync(Guid operationId, LongRestMaintenanceCapability? capability, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Synchronous acquisition keeps the gate's ambient admitted lifetime in this execution context.
        if (!admissionGate.TryAcquireWorkLease(GrimoireWorkKind.LongRestSweep, out IGrimoireWorkLease? admitted))
        {
            ct.ThrowIfCancellationRequested();

            // A known work kind is refused only after maintenance has closed admission.
            throw new LongRestMaintenanceDeferredException(CancellationToken.None);
        }

        await using IGrimoireWorkLease acquired = admitted ?? throw new InvalidOperationException("Long Rest admission returned no lease.");

        return await ResumeCoreAsync(operationId, capability, acquired, false, ct).ConfigureAwait(false);
    }

    /// <summary>The hosted worker's door: the worker's own lease arrives by value, and no capability can.</summary>
    public async Task<Result<LongRestSweepProgress>> ResumeAdmittedAsync(Guid operationId, IGrimoireWorkLease admission, CancellationToken ct)
    {
        // Admission Kind Guard: every Long Rest lease is a Long Rest sweep lease, and nothing else may carry the sweep.
        if (admission is null) throw new ArgumentNullException(nameof(admission));

        if (admission.Kind != GrimoireWorkKind.LongRestSweep) throw new ArgumentException("Long Rest work requires a Long Rest sweep admission.", nameof(admission));

        ct.ThrowIfCancellationRequested();

        return await ResumeCoreAsync(operationId, null, admission, true, ct).ConfigureAwait(false);
    }

    // hostedWorker is true only when the hosted worker's own port called: the one entry that may readmit a parked clean
    // sweep. The Session shell never sets it, whatever capability it carries.
    private async Task<Result<LongRestSweepProgress>> ResumeCoreAsync(Guid operationId,
        LongRestMaintenanceCapability? capability, IGrimoireWorkLease admission, bool hostedWorker, CancellationToken caller)
    {
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(caller, admission.MaintenanceRevocation);

        CancellationToken ct = lifetime.Token;

        if (!configuration.Capture().Enabled) return Unavailable();

        // Another executor in this process already owns this operation: nothing is written, and the owner is advancing it.
        if (!ownership.TryClaim(operationId, out Guid ownershipToken)) return Busy();

        string? owner = null;

        bool isProtected = false;

        try
        {
            LongRunningOperation operation = await operations.GetAsync(operationId, ct).ConfigureAwait(false) ?? throw new Refused(Unavailable());

            if (operation.Kind != LongRunningOperationKinds.LongRestSweep) return Unavailable();

            LongRestOperationScope stored = Require(await facts.ReadScopeAsync(operationId, ct).ConfigureAwait(false));

            isProtected = stored.Scope.ProtectedPartitionDigest is not null;

            owner = Guid.NewGuid().ToString("N");

            DateTimeOffset now = timeProvider.GetUtcNow();

            LongRunningOperation running;

            int readmitCount = 0;

            if (operation.State == LongRunningOperationState.ReconciliationRequired)
            {
                // A parked sweep is readmitted only by the authority class that owns it: the hosted worker readmits a clean
                // sweep parked with a resumable fault, and a fresh Session capability readmits a protected one.
                string code = operation.TerminalErrorCode ?? string.Empty;

                bool own = isProtected
                    ? capability is not null && code == AuthorizedRequestRequired
                    : hostedWorker && capability is null && LongRestFaultPolicy.Classify(code) == LongRestFaultClass.ResumeLater;

                if (!own) return Deferred(operation, stored.Scope, isProtected ? AuthorizedRequestRequired : code);

                LongRestSweepConfiguration options = CurrentOptions();

                if (isProtected) await ValidateCapabilityAsync(operationId, stored.Scope, capability!, options, ct).ConfigureAwait(false);

                if (sweeps is not ILongRestParkedReadmission readmission) return Unavailable();

                // One atomic transaction: ReconciliationRequired straight to Running, never through Pending.
                LongRestReadmission? readmitted = Require(await readmission.TryReadmitParkedAsync(operationId, operation.Revision, stored.Scope,
                    options.Stamp, owner, now, now.Add(LeaseDuration), ct).ConfigureAwait(false));

                if (readmitted is null) return Deferred(operation, stored.Scope, code);

                running = readmitted.Operation;

                readmitCount = readmitted.ReadmitCount;

                ArcanumMetrics.LongRestSweepReadmittedTotal.Add(1);
            }
            else
            {
                LongRunningOperationLeaseResult lease = await operations.TryAcquireLeaseAsync(operationId, owner, now, now.Add(LeaseDuration), ct).ConfigureAwait(false);

                if (!lease.Acquired)
                {
                    // A live lease held by another owner is busy; a row that cannot be leased for any other reason is unavailable.
                    return lease.Operation is { State: LongRunningOperationState.Running, LeaseOwner: not null } ? Busy() : Unavailable();
                }

                running = lease.Operation;
            }

            RunResult result = await RunOwnedAsync(running, capability, admission, ct).ConfigureAwait(false);

            LongRunningOperation latest = await OwnedAsync(operationId, owner, CancellationToken.None, allowCancelling: true).ConfigureAwait(false);

            LongRunningOperationRecoveryResult outcome = latest.State == LongRunningOperationState.Cancelling
                ? LongRunningOperationRecoveryResult.Abandoned(LongRestParkCodes.Cancelled) : result.Outcome;

            if (outcome.State == LongRunningOperationState.ReconciliationRequired)
            {
                if (!isProtected && LongRestFaultPolicy.Classify(outcome.ErrorCode) == LongRestFaultClass.RestartGeneration)
                {
                    // The captured generation can never complete. Parking it would only block its scope, so a clean sweep is retired
                    // through the Abandoned path and the next request replaces it with a new generation.
                    outcome = LongRunningOperationRecoveryResult.Abandoned(outcome.ErrorCode);
                }
                else
                {
                    // The budget is read where the park is stamped, not taken from the readmission: committed progress during the run
                    // has since reset it, and a stale count would turn a resumable fault into a needs-operator park.
                    int budget = await MarkParkedAsync(operationId, readmitCount).ConfigureAwait(false);

                    LongRunningOperationRecoveryResult stamped = outcome;

                    outcome = ApplyFaultPolicy(outcome, isProtected, budget);

                    if (outcome.ErrorCode == LongRestParkCodes.ReadmitExhausted && stamped.ErrorCode != LongRestParkCodes.ReadmitExhausted)
                    {
                        ArcanumMetrics.LongRestSweepBudgetExhaustedTotal.Add(1);
                    }
                }

                RecordParked(outcome.ErrorCode);
            }

            if (!await operations.TryTransitionAsync(operationId, latest.Revision, owner, outcome.State, timeProvider.GetUtcNow(), outcome.ErrorCode, CancellationToken.None).ConfigureAwait(false))
            {
                return Unavailable();
            }

            return result.Progress with { DeferredReason = outcome.State == LongRunningOperationState.Completed ? null : outcome.ErrorCode };
        }
        catch (Refused refusal)
        {
            return refusal.Error;
        }
        catch (OperationCanceledException exception) when (owner is not null && (exception is LongRestMaintenanceDeferredException
            || !caller.IsCancellationRequested && admission.MaintenanceRevocation.IsCancellationRequested))
        {
            // Maintenance yielded the work it was holding. Park it where its own authority class will find it again;
            // when even that write is refused, the lease expiry and the interrupted code are the fallback.
            await ParkAfterDeferralAsync(operationId, owner, isProtected).ConfigureAwait(false);

            if (exception is LongRestMaintenanceDeferredException) throw;

            throw new LongRestMaintenanceDeferredException(admission.MaintenanceRevocation);
        }
        finally
        {
            ownership.Release(operationId, ownershipToken);
        }
    }

    /// <summary>
    /// A resumable fault that spends the last of the Sweep Failure Budget stops being resumable: the park becomes a
    /// needs-operator park under its own code. A protected sweep has no budget, and free parks spend none. The count is the
    /// durable budget as the park left it, after any progress the run committed.
    /// </summary>
    private static LongRunningOperationRecoveryResult ApplyFaultPolicy(LongRunningOperationRecoveryResult outcome, bool isProtected, int readmitCount)
    {
        if (outcome.State != LongRunningOperationState.ReconciliationRequired
            || LongRestFaultPolicy.Classify(outcome.ErrorCode) != LongRestFaultClass.ResumeLater)
        {
            return outcome;
        }

        // A protected sweep has no failure budget and no worker door: whatever resumable fault parked it, only a fresh authorized
        // Session request may readmit it, so its park always says so.
        if (isProtected) return LongRunningOperationRecoveryResult.RequiresAttention(AuthorizedRequestRequired);

        return !LongRestFaultPolicy.IsFreePark(outcome.ErrorCode) && !LongRestFaultPolicy.CanReadmit(readmitCount)
            ? LongRunningOperationRecoveryResult.RequiresAttention(LongRestParkCodes.ReadmitExhausted) : outcome;
    }

    private static void RecordParked(string? code) => ArcanumMetrics.LongRestSweepParkedTotal.Add(1,
        new KeyValuePair<string, object?>("class", LongRestFaultPolicy.ClassTag(LongRestFaultPolicy.Classify(code))));

    /// <summary>
    /// Stamps the park and returns the durable Sweep Failure Budget. A missed stamp leaves the readmission's own stamp as the
    /// cooldown clock, and the budget still bounds the sweep; when the count cannot be read the caller's is used, which
    /// only progress can have lowered, so the fallback errs toward asking for an operator.
    /// </summary>
    private async Task<int> MarkParkedAsync(Guid operationId, int readmitCount)
    {
        if (sweeps is not ILongRestParkedReadmission parking) return readmitCount;

        Result<int> parked = await parking.MarkParkedAsync(operationId, timeProvider.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);

        return parked.IsSuccess ? parked.Value : readmitCount;
    }

    private async Task ParkAfterDeferralAsync(Guid operationId, string? owner, bool isProtected)
    {
        if (owner is null) return;

        try
        {
            LongRunningOperation latest = await OwnedAsync(operationId, owner, CancellationToken.None, allowCancelling: true).ConfigureAwait(false);

            // A sweep that never saved its first checkpoint has nothing to resume. It keeps its lease, and when that lapses
            // recovery abandons it as never started, which a fresh request then replaces.
            if (latest.CheckpointVersion == 0) return;

            LongRunningOperationRecoveryResult parked = latest.State == LongRunningOperationState.Cancelling
                ? LongRunningOperationRecoveryResult.Abandoned(LongRestParkCodes.Cancelled)
                : LongRunningOperationRecoveryResult.RequiresAttention(isProtected ? AuthorizedRequestRequired : LongRestParkCodes.MaintenanceDeferred);

            if (parked.State == LongRunningOperationState.ReconciliationRequired)
            {
                _ = await MarkParkedAsync(operationId, 0).ConfigureAwait(false);

                RecordParked(parked.ErrorCode);
            }

            _ = await operations.TryTransitionAsync(operationId, latest.Revision, owner, parked.State, timeProvider.GetUtcNow(), parked.ErrorCode, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort by design: the caller rethrows the deferral either way.
        }
    }

    private async Task<Result> ValidateHistoricalAsync(LongRestDiscoveryGeneration generation, CanonicalCampaignContext scope,
        LongRestSweepScope partition, LongRestSweepConfiguration options, CancellationToken ct) =>
        await IsHistoricallyCurrentAsync(generation, scope, partition, options, ct).ConfigureAwait(false) ? Result.Success() : Unavailable();

    /// <summary>
    /// The completed sweep a request is answered with, or <see langword="null"/> when the candidate is no longer one: it has been
    /// superseded, invalidated or purged since the lookup found it, or it no longer describes the installation. The operation is read
    /// before the fence is judged and the fence is judged once, after it: a completed operation never regresses, so the fence that is
    /// current at that point is the one the answer stands on.
    /// </summary>
    private async Task<LongRunningOperationDto?> ReplayCompletedAsync(Guid completedId, CanonicalCampaignContext scope,
        LongRestSweepScope partition, LongRestSweepConfiguration options, CancellationToken ct)
    {
        LongRestDiscoveryGeneration? generation = Require(await discovery.ReadGenerationMetadataAsync(completedId, ct).ConfigureAwait(false));

        if (generation is not { Phase: LongRestSweepPhase.Completed }) return null;

        LongRunningOperation? operation = await operations.GetAsync(completedId, ct).ConfigureAwait(false);

        if (operation is null) return null;

        if (operation.Kind != LongRunningOperationKinds.LongRestSweep || operation.State != LongRunningOperationState.Completed)
        {
            throw new Refused(Unavailable());
        }

        return await IsHistoricallyCurrentAsync(generation, scope, partition, options, ct).ConfigureAwait(false)
            ? LongRunningOperationDto.FromOperation(operation) : null;
    }

    /// <summary>
    /// Whether the generation still describes this installation: its scope, partition and recipe, the configuration value and the fence,
    /// judged as <see cref="LongRestFenceCurrency"/> judges it (a completed sweep by what it describes, any other by whether it can resume).
    /// A live fence that cannot be captured at all is a refusal, not a "no".
    /// </summary>
    private async Task<bool> IsHistoricallyCurrentAsync(LongRestDiscoveryGeneration generation, CanonicalCampaignContext scope,
        LongRestSweepScope partition, LongRestSweepConfiguration options, CancellationToken ct)
    {
        LongRestSweepFence current = Require(await facts.CaptureAsync(scope, options.Stamp, generation.Fence.InsertionHighWatermark, ct).ConfigureAwait(false));

        return generation.Scope == partition && generation.Recipe == options.Recipe.Value
            && generation.Fence.Configuration.ValueDigest == options.Stamp.ValueDigest
            && (generation.Phase == LongRestSweepPhase.Completed
                ? LongRestFenceCurrency.Describes(generation.Fence, current) : LongRestFenceCurrency.CanResume(generation.Fence, current));
    }

    /// <summary>
    /// Fresh-Authority Retire's proof: the borrowed turn lease is this gate's own and still held, covers exactly this Campaign root
    /// and availability generation, and was issued under the dataset and authority generations the request fence just
    /// proved current, the same facts a maintenance capability is created from. It proves authority only; it reads no protected content.
    /// </summary>
    private async ValueTask<Result> ProveRequestAuthorityAsync(CovenantTurnLease authority, CanonicalCampaignContext scope,
        LongRestSweepFence fence, LongRestSweepConfiguration options, CancellationToken ct)
    {
        if (!options.Enabled || !options.CovenantEnabled || !configuration.IsCurrent(options.Stamp)) return Unavailable();

        Result live = await gate.ValidateTurnLeaseAsync(authority, ct).ConfigureAwait(false);

        if (live.IsFailure) return live;

        CovenantOperationLeaseSnapshot snapshot = authority.Snapshot;

        return snapshot.Kind == CovenantLeaseKind.Turn && snapshot.Coverage == CovenantLeaseCoverage.Scoped
            && snapshot.Scope == (scope.IsCampaignBound ? CovenantOperationScope.ForCampaign(scope.CampaignId!.Value) : CovenantOperationScope.Global)
            && snapshot.CampaignAvailabilityGeneration == scope.CampaignAvailabilityGeneration
            && snapshot.CampaignPathRevision == scope.PathIdentityRevision
            && snapshot.DatasetGeneration == fence.DatasetGeneration
            && snapshot.RuntimeAuthorityGeneration == fence.RuntimeAuthorityGeneration
            ? Result.Success()
            : new Error(ErrorCodes.LongRest.StaleInput, "The request's turn authority does not cover this exact Long Rest scope.");
    }

    private async Task ValidateCapabilityAsync(Guid operationId, LongRestSweepScope partition, LongRestMaintenanceCapability capability,
        LongRestSweepConfiguration options, CancellationToken ct)
    {
        if (partition.ProtectedPartitionDigest is null || !options.CovenantEnabled || capability.OperationId != operationId
            || capability.CanonicalScope.CampaignId != partition.CampaignId || capability.Configuration != options.Stamp) throw new Refused(Unavailable());

        Require(await gate.ValidateTurnLeaseAsync(capability.Authority, ct).ConfigureAwait(false));

        Require(await capability.ValidateAsync(options.Stamp, ct).ConfigureAwait(false));

        LongRestDiscoveryGeneration? generation = Require(await discovery.ReadGenerationMetadataAsync(operationId, ct).ConfigureAwait(false));

        if (generation is not null)
        {
            Require(await ValidateHistoricalAsync(generation, capability.CanonicalScope, partition, options, ct).ConfigureAwait(false));

            Require(await facts.ValidateRetainedSourceMetadataAsync(generation, ct).ConfigureAwait(false));
        }
        else
        {
            Require(await facts.CaptureAsync(capability.CanonicalScope, options.Stamp, null, ct).ConfigureAwait(false));
        }

        Require(await facts.ValidateClaimAsync(capability.Claim, capability.CanonicalScope, timeProvider.GetUtcNow(), ct).ConfigureAwait(false));
    }

    private LongRestSweepConfiguration CurrentOptions()
    {
        LongRestSweepConfiguration options = configuration.Capture();

        if (!options.Enabled || !configuration.IsCurrent(options.Stamp)) throw new Refused(Unavailable());

        // An enabled configuration whose recipe cannot resolve says so, in the one content-free sentence its other readers give, instead
        // of the generic "could not be proved": the request, and so the protected turn that made it, ends with a reason.
        if (options.Recipe.IsFailure) throw new Refused(new Error(ErrorCodes.LongRest.Unavailable, options.Recipe.Error.Message));

        return options;
    }

    private static LongRestSweepProgress Deferred(LongRunningOperation operation, LongRestSweepScope scope, string reason) =>
        new(operation.Id, Guid.Empty, scope.Kind, scope.CampaignId, LongRestSweepPhase.Deferred, 0, null, reason, scope.ProtectedPartitionDigest);

    private static T Require<T>(Result<T> result) => result.IsSuccess ? result.Value : throw new Refused(result.Error);

    private static void Require(Result result)
    {
        if (result.IsFailure) throw new Refused(result.Error);
    }

    private static Error Unavailable() => new(ErrorCodes.LongRest.Unavailable, "The current Long Rest operation, worker or configuration could not be proved.");

    private static Error Busy() => new(ErrorCodes.LongRest.OperationBusy, "Another executor in this process owns this Long Rest operation right now.");

    private sealed class Refused(Error error) : Exception(error.Code)
    {
        internal Error Error { get; } = error;
    }

    private sealed record RunResult(LongRestSweepProgress Progress, LongRunningOperationRecoveryResult Outcome);
}
