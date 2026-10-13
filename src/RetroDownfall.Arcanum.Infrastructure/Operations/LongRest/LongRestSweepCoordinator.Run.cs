using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

namespace RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;

internal sealed partial class LongRestSweepCoordinator
{
    private async Task<RunResult> RunOwnedAsync(LongRunningOperation operation, LongRestMaintenanceCapability? capability, CancellationToken ct)
    {
        string owner = operation.LeaseOwner ?? throw new Refused(Unavailable());

        operation = await OwnedAsync(operation.Id, owner, ct, allowCancelling: true).ConfigureAwait(false);

        LongRestOperationScope stored = Require(await facts.ReadScopeAsync(operation.Id, ct).ConfigureAwait(false));

        LongRestSweepProgress progress = Deferred(operation, stored.Scope, "long_rest.interrupted");

        if (operation.State == LongRunningOperationState.Cancelling)
        {
            return new(progress, LongRunningOperationRecoveryResult.Abandoned("long_rest.cancelled"));
        }

        LongRestSweepCheckpoint checkpoint = operation.CheckpointVersion == 0
            ? new(1, operation.Id, stored.Scope.Kind, stored.Scope.CampaignId, null, null, 0, null, null, stored.Scope.ProtectedPartitionDigest)
            : Require(LongRestSweepCheckpointCodec.Decode(operation.Id, operation.CheckpointPayload ?? []));

        if (checkpoint.Scope != stored.Scope.Kind || checkpoint.CampaignId != stored.Scope.CampaignId
            || checkpoint.ProtectedPartitionDigest != stored.Scope.ProtectedPartitionDigest || operation.CheckpointVersion is < 0 or > 1)
        {
            throw new Refused(Unavailable());
        }

        using CancellationTokenSource dispatch = CancellationTokenSource.CreateLinkedTokenSource(ct, capability?.Revocation ?? CancellationToken.None);

        using CancellationTokenSource stop = new();

        Task heartbeat = KeepWorkerAliveAsync(operation.Id, owner, dispatch, stop.Token);

        try
        {
            try
            {
                if (operation.CheckpointVersion == 0)
                {
                    await SaveAsync(operation.Id, owner, 0, checkpoint, dispatch.Token).ConfigureAwait(false);
                }

                if (stored.Scope.ProtectedPartitionDigest is not null && capability is null)
                {
                    return new(progress, LongRunningOperationRecoveryResult.RequiresAttention(AuthorizedRequestRequired));
                }

                LongRestSweepConfiguration options = CurrentOptions();

                CanonicalCampaignContext canonical = Require(await facts.ResolveCanonicalAsync(stored.Scope, dispatch.Token).ConfigureAwait(false));

                if (capability is not null)
                {
                    await ValidateCapabilityAsync(operation.Id, stored.Scope, capability, options, dispatch.Token).ConfigureAwait(false);

                    if (capability.CanonicalScope != canonical) throw new Refused(Unavailable());

                    Require(await capability.ConsumeAsync(operation.Id, stored.Scope, options.Stamp, dispatch.Token).ConfigureAwait(false));
                }

                LongRestDiscoveryGeneration? previous = Require(await discovery.ReadGenerationMetadataAsync(operation.Id, dispatch.Token).ConfigureAwait(false));

                if (checkpoint.GenerationId is not null)
                {
                    if (previous is null) throw new Refused(Unavailable());

                    Require(await facts.ReadWorkerAsync(previous, owner, timeProvider.GetUtcNow(), dispatch.Token).ConfigureAwait(false));
                }

                LongRestSweepFence current = Require(await facts.CaptureAsync(canonical, options.Stamp,
                    previous?.Fence.InsertionHighWatermark, dispatch.Token).ConfigureAwait(false));

                if (previous?.Phase == LongRestSweepPhase.Completed)
                {
                    if (previous.Fence != current && previous.Fence.Configuration.BootId == current.Configuration.BootId
                        && stored.Scope.ProtectedPartitionDigest is null && discovery is ILongRestCompletedCheckpointReconciliation completedRecovery
                        && facts.AttestCleanAdmission(workAdmission.Lease, previous.Fence.AdmissionGeneration, current.AdmissionGeneration))
                    {
                        LongRestSweep historical = Require(await completedRecovery.ReconcileCompletedCheckpointAsync(previous, current,
                            options.Recipe.Value, owner, facts, workAdmission.Lease, dispatch.Token).ConfigureAwait(false));

                        return new(Progress(historical), LongRunningOperationRecoveryResult.Completed());
                    }

                    Require(await ValidateHistoricalAsync(previous, canonical, stored.Scope, options, dispatch.Token).ConfigureAwait(false));

                    Guid? completed = Require(await sweeps.FindCompletedReplayAsync(canonical, stored.Scope, options.Recipe.Value, dispatch.Token).ConfigureAwait(false));

                    if (completed != operation.Id) throw new Refused(Unavailable());
                }
                else
                {
                    await OwnedAsync(operation.Id, owner, dispatch.Token).ConfigureAwait(false);

                    if (previous is not null && previous.Fence != current
                        && previous.Fence.Configuration.BootId == current.Configuration.BootId
                        && stored.Scope.ProtectedPartitionDigest is null && discovery is ILongRestCleanReadmission clean
                        && facts.AttestCleanAdmission(workAdmission.Lease, previous.Fence.AdmissionGeneration, current.AdmissionGeneration))
                    {
                        Require(await clean.ReadmitCleanAsync(previous, current, options.Recipe.Value, owner, facts, workAdmission.Lease, dispatch.Token).ConfigureAwait(false));
                    }
                    else
                    {
                        Require(await discovery.StartOrResumeAsync(operation.Id, stored.Scope, current, options.Recipe.Value, dispatch.Token).ConfigureAwait(false));
                    }
                }

                LongRestSweep committed = Require(await sweeps.BeginOrResumeAsync(operation.Id, stored.Scope,
                    previous?.Phase == LongRestSweepPhase.Completed ? previous.Fence.Configuration : options.Stamp, dispatch.Token).ConfigureAwait(false));

                if (checkpoint.GenerationId is { } generation && generation != committed.GenerationId || checkpoint.Revision > committed.Revision)
                {
                    throw new Refused(Unavailable());
                }

                // The initial checkpoint preceded generation creation; bind it to the retained committed
                // generation before the first source read, provider dispatch or canonical transformation.
                await SaveAsync(operation.Id, owner, 1, Checkpoint(committed), dispatch.Token).ConfigureAwait(false);

                progress = Progress(committed);

                while (committed.Phase != LongRestSweepPhase.Completed)
                {
                    await OwnedAsync(operation.Id, owner, dispatch.Token).ConfigureAwait(false);

                    progress = Require(await maintenance.ProcessAsync(operation.Id, owner, capability, dispatch.Token).ConfigureAwait(false));

                    await OwnedAsync(operation.Id, owner, dispatch.Token).ConfigureAwait(false);

                    committed = Require(await sweeps.BeginOrResumeAsync(operation.Id, stored.Scope, options.Stamp, dispatch.Token).ConfigureAwait(false));

                    await SaveAsync(operation.Id, owner, 1, Checkpoint(committed), dispatch.Token).ConfigureAwait(false);
                }

                return new(Progress(committed), LongRunningOperationRecoveryResult.Completed());
            }
            catch (Refused refusal)
            {
                return new(progress with { DeferredReason = refusal.Error.Code }, LongRunningOperationRecoveryResult.RequiresAttention(refusal.Error.Code));
            }
            catch (LongRestMaintenanceDeferredException)
            {
                throw;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new(progress, LongRunningOperationRecoveryResult.RequiresAttention(stored.Scope.ProtectedPartitionDigest is null
                    ? "long_rest.worker_unavailable" : AuthorizedRequestRequired));
            }
            finally
            {
                await stop.CancelAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await heartbeat.ConfigureAwait(false);
        }
    }

    private async Task SaveAsync(Guid operation, string owner, int expectedVersion, LongRestSweepCheckpoint checkpoint, CancellationToken ct)
    {
        await OwnedAsync(operation, owner, ct).ConfigureAwait(false);

        if (!await operations.SaveCheckpointAsync(operation, owner, expectedVersion, 1, Require(LongRestSweepCheckpointCodec.Encode(checkpoint)),
            null, "Long Rest sweep progress committed.", timeProvider.GetUtcNow(), ct).ConfigureAwait(false)) throw new Refused(Unavailable());
    }

    private async Task<LongRunningOperation> OwnedAsync(Guid operation, string owner, CancellationToken ct, bool allowCancelling = false)
    {
        LongRunningOperation latest = await operations.GetAsync(operation, ct).ConfigureAwait(false) ?? throw new Refused(Unavailable());

        if (latest.Kind != LongRunningOperationKinds.LongRestSweep || latest.LeaseOwner != owner || string.IsNullOrWhiteSpace(owner)
            || latest.LeaseExpiresAt is not { } expiry || expiry <= timeProvider.GetUtcNow()
            || latest.State != LongRunningOperationState.Running && !(allowCancelling && latest.State == LongRunningOperationState.Cancelling))
        {
            throw new Refused(Unavailable());
        }

        return latest;
    }

    private async Task KeepWorkerAliveAsync(Guid operation, string owner, CancellationTokenSource dispatch, CancellationToken stoppingToken)
    {
        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromMinutes(1), timeProvider);

            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();

                ILongRunningOperationStore independent = scope.ServiceProvider.GetRequiredService<ILongRunningOperationStore>();

                LongRunningOperation? latest = await independent.GetAsync(operation, stoppingToken).ConfigureAwait(false);

                DateTimeOffset now = timeProvider.GetUtcNow();

                if (latest is null || latest.LeaseOwner != owner || latest.State != LongRunningOperationState.Running
                    || !await independent.RenewLeaseAsync(operation, owner, now, now.Add(LeaseDuration), stoppingToken).ConfigureAwait(false))
                {
                    await dispatch.CancelAsync().ConfigureAwait(false);

                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            await dispatch.CancelAsync().ConfigureAwait(false);
        }
    }

    private static LongRestSweepCheckpoint Checkpoint(LongRestSweep sweep) => new(1, sweep.OperationId, sweep.Scope.Kind,
        sweep.Scope.CampaignId, sweep.GenerationId == Guid.Empty ? null : sweep.GenerationId,
        sweep.GenerationId == Guid.Empty ? null : sweep.Phase, sweep.Revision, sweep.CommittedItemCursor,
        sweep.CommittedWorkDigest, sweep.Scope.ProtectedPartitionDigest);

    private static LongRestSweepProgress Progress(LongRestSweep sweep) => new(sweep.OperationId, sweep.GenerationId, sweep.Scope.Kind,
        sweep.Scope.CampaignId, sweep.Phase, sweep.Revision, sweep.CommittedItemCursor, null, sweep.Scope.ProtectedPartitionDigest);
}
