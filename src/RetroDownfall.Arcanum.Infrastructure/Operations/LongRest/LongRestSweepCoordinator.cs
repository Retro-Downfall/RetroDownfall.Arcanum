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
    ILongRestMaintenance maintenance, ICovenantOperationGate gate, LongRunningOperationOwnership ownership,
    LongRestSweepQueue queue, IServiceScopeFactory scopes, TimeProvider timeProvider, LongRestWorkAdmission workAdmission) : ILongRestSweepCoordinator
{
    internal const string AuthorizedRequestRequired = "long_rest.authorized_request_required";

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

    public async Task<Result<LongRestSweepProgress>> ResumeAsync(Guid operationId, LongRestMaintenanceCapability? capability, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (workAdmission.HasLease)
        {
            return await ResumeAdmittedAsync(operationId, capability, workAdmission.Lease, ct).ConfigureAwait(false);
        }

        await using IGrimoireWorkLease acquired = workAdmission.Acquire(ct);

        using IDisposable binding = workAdmission.Bind(acquired);

        return await ResumeAdmittedAsync(operationId, capability, acquired, ct).ConfigureAwait(false);
    }

    private async Task<Result<LongRestSweepProgress>> ResumeAdmittedAsync(Guid operationId,
        LongRestMaintenanceCapability? capability, IGrimoireWorkLease admission, CancellationToken ct)
    {
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, admission.MaintenanceRevocation);

        ct = lifetime.Token;

        if (!configuration.Capture().Enabled || !ownership.TryClaim(operationId, out Guid ownershipToken)) return Unavailable();

        try
        {
            LongRunningOperation operation = await operations.GetAsync(operationId, ct).ConfigureAwait(false) ?? throw new Refused(Unavailable());

            if (operation.Kind != LongRunningOperationKinds.LongRestSweep) return Unavailable();

            LongRestOperationScope stored = Require(await facts.ReadScopeAsync(operationId, ct).ConfigureAwait(false));

            if (operation.State == LongRunningOperationState.ReconciliationRequired)
            {
                if (capability is null || operation.TerminalErrorCode != AuthorizedRequestRequired)
                {
                    return Deferred(operation, stored.Scope, AuthorizedRequestRequired);
                }

                await ValidateCapabilityAsync(operationId, stored.Scope, capability, CurrentOptions(), ct).ConfigureAwait(false);

                if (!await operations.ResetForRetryAsync(operationId, operation.Revision, timeProvider.GetUtcNow(), ct).ConfigureAwait(false)) return Unavailable();
            }

            DateTimeOffset now = timeProvider.GetUtcNow();

            string owner = Guid.NewGuid().ToString("N");

            LongRunningOperationLeaseResult lease = await operations.TryAcquireLeaseAsync(operationId, owner, now, now.Add(LeaseDuration), ct).ConfigureAwait(false);

            if (!lease.Acquired) return Unavailable();

            RunResult result = await RunOwnedAsync(lease.Operation, capability, ct).ConfigureAwait(false);

            LongRunningOperation latest = await OwnedAsync(operationId, owner, CancellationToken.None, allowCancelling: true).ConfigureAwait(false);

            LongRunningOperationRecoveryResult outcome = latest.State == LongRunningOperationState.Cancelling
                ? LongRunningOperationRecoveryResult.Abandoned("long_rest.cancelled") : result.Outcome;

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
        finally
        {
            ownership.Release(operationId, ownershipToken);
        }
    }

    internal async Task<LongRunningOperationRecoveryResult> RecoverAsync(LongRunningOperation operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (workAdmission.HasLease)
        {
            return await RecoverAdmittedAsync(operation, workAdmission.Lease, ct).ConfigureAwait(false);
        }

        await using IGrimoireWorkLease acquired = workAdmission.Acquire(ct);

        using IDisposable binding = workAdmission.Bind(acquired);

        return await RecoverAdmittedAsync(operation, acquired, ct).ConfigureAwait(false);
    }

    private async Task<LongRunningOperationRecoveryResult> RecoverAdmittedAsync(LongRunningOperation operation,
        IGrimoireWorkLease admission, CancellationToken caller)
    {
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(caller, admission.MaintenanceRevocation);

        CancellationToken ct = lifetime.Token;

        if (!ownership.TryClaim(operation.Id, out Guid ownershipToken)) return LongRunningOperationRecoveryResult.RequiresAttention("long_rest.worker_unavailable");

        try
        {
            RunResult result = await RunOwnedAsync(operation, null, ct).ConfigureAwait(false);

            LongRunningOperation latest = await OwnedAsync(operation.Id, operation.LeaseOwner!, ct, allowCancelling: true).ConfigureAwait(false);

            return latest.State == LongRunningOperationState.Cancelling
                ? LongRunningOperationRecoveryResult.Abandoned("long_rest.cancelled") : result.Outcome;
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested && admission.MaintenanceRevocation.IsCancellationRequested)
        {
            throw new LongRestMaintenanceDeferredException(admission.MaintenanceRevocation);
        }
        catch (Refused refusal)
        {
            return LongRunningOperationRecoveryResult.RequiresAttention(refusal.Error.Code);
        }
        finally
        {
            ownership.Release(operation.Id, ownershipToken);
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
