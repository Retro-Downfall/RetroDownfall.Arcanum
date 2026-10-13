using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
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

    public async Task<Result<LongRunningOperationDto>> RequestAsync(CanonicalCampaignContext scope, LongRestSweepScope partition, CancellationToken ct)
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

            if (completed is { } completedId)
            {
                LongRestDiscoveryGeneration generation = Require(await discovery.ReadGenerationMetadataAsync(completedId, ct).ConfigureAwait(false))
                    ?? throw new Refused(Unavailable());

                Require(await ValidateHistoricalAsync(generation, scope, partition, options, ct).ConfigureAwait(false));

                LongRunningOperation operation = await operations.GetAsync(completedId, ct).ConfigureAwait(false) ?? throw new Refused(Unavailable());

                if (operation.Kind != LongRunningOperationKinds.LongRestSweep || operation.State != LongRunningOperationState.Completed)
                {
                    return Unavailable();
                }

                Require(await ValidateHistoricalAsync(generation, scope, partition, options, ct).ConfigureAwait(false));

                return LongRunningOperationDto.FromOperation(operation);
            }

            LongRestSweepFence fence = Require(await facts.CaptureAsync(scope, options.Stamp, null, ct).ConfigureAwait(false));

            if (sweeps is not ILongRestSweepRequestAdmission requests) return Unavailable();

            LongRunningOperation admitted = Require(await requests.AdmitRequestAsync(partition, fence, facts, ownership, ct).ConfigureAwait(false));

            if (partition.ProtectedPartitionDigest is null) queue.Signal(admitted.Id);

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

        if (!configuration.Capture().Enabled || !ownership.TryClaim(operationId, out Guid ownershipToken)) return Unavailable();

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
            }
            else
            {
                LongRunningOperationLeaseResult lease = await operations.TryAcquireLeaseAsync(operationId, owner, now, now.Add(LeaseDuration), ct).ConfigureAwait(false);

                if (!lease.Acquired) return Unavailable();

                running = lease.Operation;
            }

            RunResult result = await RunOwnedAsync(running, capability, admission, ct).ConfigureAwait(false);

            LongRunningOperation latest = await OwnedAsync(operationId, owner, CancellationToken.None, allowCancelling: true).ConfigureAwait(false);

            LongRunningOperationRecoveryResult outcome = latest.State == LongRunningOperationState.Cancelling
                ? LongRunningOperationRecoveryResult.Abandoned(LongRestParkCodes.Cancelled) : result.Outcome;

            if (outcome.State == LongRunningOperationState.ReconciliationRequired)
            {
                // The budget is read where the park is stamped, not taken from the readmission: committed progress during the run
                // has since reset it, and a stale count would turn a resumable fault into a needs-operator park.
                int budget = await MarkParkedAsync(operationId, readmitCount).ConfigureAwait(false);

                outcome = ApplyFaultPolicy(outcome, isProtected, budget);
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
    private static LongRunningOperationRecoveryResult ApplyFaultPolicy(LongRunningOperationRecoveryResult outcome, bool isProtected, int readmitCount) =>
        outcome.State == LongRunningOperationState.ReconciliationRequired && !isProtected
            && LongRestFaultPolicy.Classify(outcome.ErrorCode) == LongRestFaultClass.ResumeLater
            && !LongRestFaultPolicy.IsFreePark(outcome.ErrorCode)
            && !LongRestFaultPolicy.CanReadmit(readmitCount)
            ? LongRunningOperationRecoveryResult.RequiresAttention(LongRestParkCodes.ReadmitExhausted) : outcome;

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

            if (parked.State == LongRunningOperationState.ReconciliationRequired) _ = await MarkParkedAsync(operationId, 0).ConfigureAwait(false);

            _ = await operations.TryTransitionAsync(operationId, latest.Revision, owner, parked.State, timeProvider.GetUtcNow(), parked.ErrorCode, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort by design: the caller rethrows the deferral either way.
        }
    }

    private async Task<Result> ValidateHistoricalAsync(LongRestDiscoveryGeneration generation, CanonicalCampaignContext scope,
        LongRestSweepScope partition, LongRestSweepConfiguration options, CancellationToken ct)
    {
        LongRestSweepFence current = Require(await facts.CaptureAsync(scope, options.Stamp, generation.Fence.InsertionHighWatermark, ct).ConfigureAwait(false));

        return generation.Scope == partition && generation.Recipe == options.Recipe.Value && generation.Fence.CanonicalScope == scope
            && generation.Fence.DatasetGeneration == current.DatasetGeneration && generation.Fence.Configuration.ValueDigest == options.Stamp.ValueDigest
            && (generation.Fence.Configuration.BootId != current.Configuration.BootId || generation.Fence == current)
            ? Result.Success() : Unavailable();
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

        if (!options.Enabled || options.Recipe.IsFailure || !configuration.IsCurrent(options.Stamp)) throw new Refused(Unavailable());

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

    private sealed class Refused(Error error) : Exception(error.Code)
    {
        internal Error Error { get; } = error;
    }

    private sealed record RunResult(LongRestSweepProgress Progress, LongRunningOperationRecoveryResult Outcome);
}
