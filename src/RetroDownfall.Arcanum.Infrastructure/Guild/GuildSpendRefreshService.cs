using System.Text.Json;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildSpendRefreshResult(Guid OperationId, GuildSpendReceiptDto? Receipt, bool Replayed, bool InFlight,
    bool RequiresAttention = false, string? ErrorCode = null);

/// <summary>Coordinates one admitted provider observation under durable and physical ownership.</summary>
internal sealed class GuildSpendRefreshService(GuildQuestStore quests, GuildSpendRefreshConfiguration configuration,
    IProviderApiKeyResolver credentials, GuildOpenRouterSpendReader reader, IGuildLocalHostReadiness readiness,
    IGrimoireConnectionAdmissionGate gate, ILongRunningOperationStore operations, LongRunningOperationOwnership ownership,
    TimeProvider time, GuildSpendRefreshTracker tracker)
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    internal async Task<Result<GuildSpendRefreshResult>> RefreshAsync(GuildCallerContext caller, Guid questId, Guid effectId,
        Guid refreshId, CancellationToken ct)
    {
        Result<GuildSpendRefreshPreparation> prepared = await quests.PrepareProviderSpendRefreshAsync(caller, questId, effectId, refreshId, ct).ConfigureAwait(false);

        if (prepared.IsFailure) return prepared.Error;

        if (prepared.Value.OperationId is { } prior)
            return await DescribeExistingAsync(prior, prepared.Value.Receipt, ct).ConfigureAwait(false);

        if (prepared.Value.Provider is not { } provider || prepared.Value.Model is not { } model) return Unavailable;

        Result<GuildSpendRefreshCurrentness> before = await GuildSpendRefreshCurrentness.CaptureAsync(configuration, credentials, provider, model, ct).ConfigureAwait(false);

        if (before.IsFailure) return before.Error;

        if (prepared.Value.RootQuestId is not { } root || !tracker.TryEnter(root, out GuildSpendRefreshTracker.Entry? entry) || entry is null)
            return Unavailable;

        using (entry)
        using (CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, entry.Stop))
        {
            Result<GuildSpendRefreshPreparation> admitted = await quests.AdmitProviderSpendRefreshAsync(caller, questId, effectId,
                refreshId, before.Value, lifetime.Token).ConfigureAwait(false);

            if (admitted.IsFailure) return admitted.Error;

            if (admitted.Value.OperationId is not { } operationId) return Unavailable;

            if (!admitted.Value.Created)
                return await DescribeExistingAsync(operationId, admitted.Value.Receipt, lifetime.Token).ConfigureAwait(false);

            if (!ownership.TryClaim(operationId, out Guid claim)) return new GuildSpendRefreshResult(operationId, null, false, true);

            try
            {
                Result<GuildSpendRefreshResult> attempted = await RunClaimedAsync(caller, questId, effectId, refreshId, operationId,
                    provider, model, before.Value, entry, lifetime.Token).ConfigureAwait(false);

                return attempted.IsSuccess ? attempted : new GuildSpendRefreshResult(operationId, null, false, false, true,
                    ErrorCodes.Guild.ReconciliationRequired);
            }
            catch (Exception)
            {
                return new GuildSpendRefreshResult(operationId, null, false, false, true, ErrorCodes.Guild.ReconciliationRequired);
            }
            finally
            {
                ownership.Release(operationId, claim);
            }
        }
    }

    internal async Task<LongRunningOperationRecoveryResult> RecoverClaimedAsync(GuildCallerContext caller, LongRunningOperation operation,
        CancellationToken ct)
    {
        if (operation.Kind != LongRunningOperationKinds.GuildSpendRefresh
            || operation.RecoveryPolicy != LongRunningOperationRecoveryPolicy.ReconcileAndComplete || operation.CheckpointVersion != 1
            || operation.CheckpointPayload is not { Length: > 0 and <= 2048 } payload || !ownership.IsClaimed(operation.Id))
            return LongRunningOperationRecoveryResult.RequiresAttention(ErrorCodes.Guild.Unavailable);

        GuildSpendRefreshCheckpoint? checkpoint;

        try { checkpoint = JsonSerializer.Deserialize(payload, GuildSpendRefreshCheckpointJsonContext.Default.GuildSpendRefreshCheckpoint); }
        catch (JsonException) { return LongRunningOperationRecoveryResult.RequiresAttention(ErrorCodes.Guild.Unavailable); }

        if (checkpoint is null || checkpoint.Version != 1 || checkpoint.Phase != "admitted" || checkpoint.OperationId != operation.Id
            || checkpoint.InstallationGeneration != caller.InstallationGeneration || checkpoint.PrincipalId != caller.PrincipalId
            || !payload.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(checkpoint, GuildSpendRefreshCheckpointJsonContext.Default.GuildSpendRefreshCheckpoint)))
            return LongRunningOperationRecoveryResult.RequiresAttention(ErrorCodes.Guild.Unavailable);

        Result<GuildSpendRefreshPreparation> replay = await quests.PrepareProviderSpendRefreshAsync(caller, checkpoint.QuestId,
            checkpoint.EffectId, checkpoint.RefreshId, ct).ConfigureAwait(false);

        if (replay.IsFailure) return LongRunningOperationRecoveryResult.RequiresAttention(replay.Error.Code);

        if (replay.Value.OperationId != operation.Id) return LongRunningOperationRecoveryResult.RequiresAttention(ErrorCodes.Guild.Unavailable);

        if (replay.Value.Receipt is not null) return LongRunningOperationRecoveryResult.Completed();

        if (operation.State is not (LongRunningOperationState.Running or LongRunningOperationState.Waiting)
            || string.IsNullOrWhiteSpace(operation.LeaseOwner) || operation.LeaseExpiresAt <= time.GetUtcNow())
            return LongRunningOperationRecoveryResult.RequiresAttention(ErrorCodes.Guild.Unavailable);

        Result<GuildSpendRefreshPreparation> selected = await quests.SelectProviderSpendRefreshSourceAsync(caller,
            checkpoint.QuestId, checkpoint.EffectId, ct).ConfigureAwait(false);

        if (selected.IsFailure) return LongRunningOperationRecoveryResult.RequiresAttention(selected.Error.Code);

        if (selected.Value.RootQuestId != checkpoint.RootQuestId
            || selected.Value.Provider is not { } provider || selected.Value.Model is not { } model)
            return LongRunningOperationRecoveryResult.RequiresAttention(ErrorCodes.Guild.Unavailable);

        Result<GuildSpendRefreshCurrentness> before = await GuildSpendRefreshCurrentness.CaptureAsync(configuration, credentials, provider, model, ct).ConfigureAwait(false);

        if (before.IsFailure) return LongRunningOperationRecoveryResult.RequiresAttention(before.Error.Code);

        if (!tracker.TryEnter(checkpoint.RootQuestId, out GuildSpendRefreshTracker.Entry? entry) || entry is null)
            return LongRunningOperationRecoveryResult.RequiresAttention(ErrorCodes.Guild.Unavailable);

        using (entry)
        using (CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, entry.Stop))
        {
            Result<GuildSpendRefreshResult> result = await RunClaimedAsync(caller, checkpoint.QuestId, checkpoint.EffectId,
                checkpoint.RefreshId, operation.Id, provider, model, before.Value, entry, lifetime.Token, operation.LeaseOwner).ConfigureAwait(false);

            return result.IsSuccess && result.Value.Receipt is not null
                ? LongRunningOperationRecoveryResult.Completed()
                : LongRunningOperationRecoveryResult.RequiresAttention(result.IsFailure ? result.Error.Code : result.Value.ErrorCode ?? ErrorCodes.Guild.Unavailable);
        }
    }

    private async Task<Result<GuildSpendRefreshResult>> DescribeExistingAsync(Guid operationId, GuildSpendReceiptDto? receipt, CancellationToken ct)
    {
        if (receipt is not null) return new GuildSpendRefreshResult(operationId, receipt, true, false);

        LongRunningOperation? operation;

        try
        {
            operation = await operations.GetAsync(operationId, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return new GuildSpendRefreshResult(operationId, null, false, false, true, ErrorCodes.Guild.ReconciliationRequired);
        }

        if (operation is null || operation.Kind != LongRunningOperationKinds.GuildSpendRefresh
            || operation.RecoveryPolicy != LongRunningOperationRecoveryPolicy.ReconcileAndComplete)
            return new GuildSpendRefreshResult(operationId, null, false, false, true, ErrorCodes.Guild.ReconciliationRequired);

        return operation.State switch
        {
            LongRunningOperationState.Pending or LongRunningOperationState.Running or LongRunningOperationState.Waiting or LongRunningOperationState.Cancelling =>
                new GuildSpendRefreshResult(operationId, null, false, true),
            LongRunningOperationState.ReconciliationRequired => new GuildSpendRefreshResult(operationId, null, false, false, true,
                operation.TerminalErrorCode is ErrorCodes.Guild.Unavailable or ErrorCodes.Guild.SourceExpired
                    or ErrorCodes.Guild.SourceConflict or ErrorCodes.Guild.InitialEvidenceUnavailable
                    ? operation.TerminalErrorCode : ErrorCodes.Guild.ReconciliationRequired),
            _ => new GuildSpendRefreshResult(operationId, null, false, false, true, ErrorCodes.Guild.ReconciliationRequired),
        };
    }

    private async Task<Result<GuildSpendRefreshResult>> RunClaimedAsync(GuildCallerContext caller, Guid questId, Guid effectId,
        Guid refreshId, Guid operationId, string provider, string model, GuildSpendRefreshCurrentness before,
        GuildSpendRefreshTracker.Entry entry, CancellationToken ct, string? recoveryOwner = null)
    {
        Result<IDisposable> retained = readiness.RetainPhysicalWorkOwnership();

        if (retained.IsFailure) return recoveryOwner is null
            ? await ParkUnleasedAsync(operationId, retained.Error).ConfigureAwait(false)
            : new GuildSpendRefreshResult(operationId, null, false, false, true, retained.Error.Code);

        using IDisposable physical = retained.Value;

        if (!gate.TryAcquireWorkLease(GrimoireWorkKind.GuildStorage, out IGrimoireWorkLease? acquired) || acquired is null)
            return recoveryOwner is null ? await ParkUnleasedAsync(operationId, Unavailable).ConfigureAwait(false)
                : new GuildSpendRefreshResult(operationId, null, false, false, true);

        await using IGrimoireWorkLease work = acquired;

        if (!work.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? group) || group is null)
            return recoveryOwner is null ? await ParkUnleasedAsync(operationId, Unavailable).ConfigureAwait(false)
                : new GuildSpendRefreshResult(operationId, null, false, false, true);

        await using IGrimoireExternalEffectGroup heldGroup = group;

        using GuildSpendRefreshAttemptGuard guard = new(gate, work, heldGroup, physical, entry, ownership, operationId);

        string owner = recoveryOwner ?? "guild-refresh-" + Guid.NewGuid().ToString("N");

        if (recoveryOwner is null)
        {
            DateTimeOffset now = time.GetUtcNow();

            LongRunningOperationLeaseResult lease;

            try
            {
                lease = await operations.TryAcquireLeaseAsync(operationId, owner, now, now.Add(LeaseDuration), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LongRunningOperation? uncertain = await operations.GetAsync(operationId, CancellationToken.None).ConfigureAwait(false);

                return uncertain?.LeaseOwner == owner ? await ParkAsync(operationId, owner, Unavailable).ConfigureAwait(false)
                    : await ParkUnleasedAsync(operationId, Unavailable).ConfigureAwait(false);
            }

            if (!lease.Acquired) return new GuildSpendRefreshResult(operationId, null, false, true);
        }

        using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(ct, work.MaintenanceRevocation);

        using CancellationTokenSource stopHeartbeat = new();

        Task heartbeat = MonitorAsync(operationId, owner, work, attempt, stopHeartbeat.Token);

        Result<GuildSpendCorrectionResultDto> result;

        try
        {
            result = await ObserveAndApplyAsync(caller, questId, effectId, refreshId, owner, provider, model, before, guard, attempt.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The admitted operation must retain its identity even if transport disposal fails.
            result = Unavailable;
        }
        finally
        {
            await stopHeartbeat.CancelAsync().ConfigureAwait(false);

            await heartbeat.ConfigureAwait(false);
        }

        if (result.IsFailure) return recoveryOwner is null ? await ParkAsync(operationId, owner, result.Error).ConfigureAwait(false)
            : new GuildSpendRefreshResult(operationId, null, false, false, true, result.Error.Code);

        if (recoveryOwner is not null) return new GuildSpendRefreshResult(operationId, result.Value.Receipt, result.Value.Replayed, false);

        LongRunningOperation? latest = await operations.GetAsync(operationId, CancellationToken.None).ConfigureAwait(false);

        if (latest is not null && latest.LeaseOwner == owner && latest.LeaseExpiresAt > time.GetUtcNow())
            _ = await operations.TryTransitionAsync(operationId, latest.Revision, owner, LongRunningOperationState.Completed,
                time.GetUtcNow(), cancellationToken: CancellationToken.None).ConfigureAwait(false);

        return new GuildSpendRefreshResult(operationId, result.Value.Receipt, result.Value.Replayed, false);
    }

    private async Task<Result<GuildSpendCorrectionResultDto>> ObserveAndApplyAsync(GuildCallerContext caller, Guid questId,
        Guid effectId, Guid refreshId, string owner, string provider, string model, GuildSpendRefreshCurrentness before,
        GuildSpendRefreshAttemptGuard guard, CancellationToken ct)
    {
        Result<GuildSpendSourceCredential.VerifiedSource> source = await quests.VerifyProviderSpendRefreshSourceAsync(caller, questId,
            effectId, refreshId, owner, before, guard, ct).ConfigureAwait(false);

        if (source.IsFailure) return source.Error;

        Result<GuildSpendObservation> observed = await reader.ReadAsync(source.Value, ct).ConfigureAwait(false);

        if (observed.IsFailure) return observed.Error;

        if (!before.IsCurrent()) return Unavailable;

        Result<GuildSpendRefreshCurrentness> after = await GuildSpendRefreshCurrentness.CaptureAsync(configuration, credentials, provider, model, ct).ConfigureAwait(false);

        if (after.IsFailure || !before.IsCurrent()) return Unavailable;

        return await quests.ApplyProviderSpendRefreshAsync(caller, questId, effectId, refreshId, owner,
            after.Value, observed.Value, guard, ct).ConfigureAwait(false);
    }

    private async Task MonitorAsync(Guid operationId, string owner, IGrimoireWorkLease work,
        CancellationTokenSource attempt, CancellationToken stop)
    {
        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromSeconds(1), time);

            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                LongRunningOperation? operation = await operations.GetAsync(operationId, stop).ConfigureAwait(false);

                if (work.MaintenanceRevocationPending || work.Generation != gate.CurrentGeneration || readiness.RequireExecutionReady().IsFailure
                    || operation is null || operation.LeaseOwner != owner || operation.LeaseExpiresAt <= time.GetUtcNow()
                    || operation.State is not (LongRunningOperationState.Running or LongRunningOperationState.Waiting))
                {
                    await attempt.CancelAsync().ConfigureAwait(false);

                    return;
                }

                DateTimeOffset now = time.GetUtcNow();

                if (!await operations.RenewLeaseAsync(operationId, owner, now, now.Add(LeaseDuration), stop).ConfigureAwait(false))
                {
                    await attempt.CancelAsync().ConfigureAwait(false);

                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            await attempt.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task<Result<GuildSpendRefreshResult>> ParkUnleasedAsync(Guid operationId, Error error)
    {
        LongRunningOperation? latest = await operations.GetAsync(operationId, CancellationToken.None).ConfigureAwait(false);

        if (latest is null) return error;

        if (latest.State == LongRunningOperationState.Pending && latest.LeaseOwner is null
            && await operations.TryTransitionAsync(operationId, latest.Revision, null, LongRunningOperationState.ReconciliationRequired,
                time.GetUtcNow(), error.Code, CancellationToken.None).ConfigureAwait(false))
            return new GuildSpendRefreshResult(operationId, null, false, false, true, error.Code);

        return new GuildSpendRefreshResult(operationId, null, false, true);
    }

    private async Task<Result<GuildSpendRefreshResult>> ParkAsync(Guid operationId, string owner, Error error)
    {
        LongRunningOperation? latest = await operations.GetAsync(operationId, CancellationToken.None).ConfigureAwait(false);

        if (latest is null) return error;

        if (latest.LeaseOwner == owner && latest.LeaseExpiresAt > time.GetUtcNow()
            && latest.State is LongRunningOperationState.Running or LongRunningOperationState.Waiting
            && await operations.TryTransitionAsync(operationId, latest.Revision, owner, LongRunningOperationState.ReconciliationRequired,
                time.GetUtcNow(), error.Code, CancellationToken.None).ConfigureAwait(false))
            return new GuildSpendRefreshResult(operationId, null, false, false, true, error.Code);

        return new GuildSpendRefreshResult(operationId, null, false, true);
    }

    private static Error Unavailable => new(ErrorCodes.Guild.Unavailable, "Provider spend refresh is unavailable.");
}
