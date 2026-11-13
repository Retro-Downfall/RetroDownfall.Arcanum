using System.Collections.Immutable;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Operations;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildSpendRefreshResult(Guid OperationId, GuildSpendReceiptDto? Receipt, bool Replayed, bool InFlight,
    bool RequiresAttention = false, string? ErrorCode = null, bool Cancelled = false);

/// <summary>Coordinates one admitted provider observation under durable and physical ownership.</summary>
internal sealed class GuildSpendRefreshService(GuildQuestStore quests, GuildSpendRefreshConfiguration configuration,
    IProviderApiKeyResolver credentials, GuildOpenRouterSpendReader reader, IGuildLocalHostReadiness readiness,
    IGrimoireConnectionAdmissionGate gate, ILongRunningOperationStore operations, LongRunningOperationOwnership ownership,
    TimeProvider time, GuildSpendRefreshTracker tracker)
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    internal async Task<Result<GuildSpendEffectDto>> ReadEffectAsync(GuildCallerContext caller, Guid questId, Guid effectId, CancellationToken ct)
    {
        Result<GuildSpendEffectDto> effect = await quests.ReadOperatorSpendEffectAsync(caller, questId, effectId, ct).ConfigureAwait(false);

        if (effect.IsFailure) return effect.Error;

        Dictionary<(string Provider, string Model), Result<GuildSpendRefreshCurrentness>> observations = [];

        var prepared = await PrepareEligibilityAsync(caller, effect.Value, observations, ct).ConfigureAwait(false);

        if (prepared.Currentness is null) return prepared.Effect;

        Result<Dictionary<Guid, Result>> checkedBatch = await quests.CheckProviderRefreshEligibilityBatchAsync(caller, questId,
            [(effectId, prepared.Currentness)], ct).ConfigureAwait(false);

        return ApplyEligibilityResult(prepared.Effect, checkedBatch);
    }

    internal async Task<Result<GuildSpendEffectPageDto>> ListEffectsAsync(GuildCallerContext caller, Guid questId, string? cursor,
        int pageSize, CancellationToken ct)
    {
        Result<GuildSpendEffectPageDto> page = await quests.ListOperatorSpendEffectsAsync(caller, questId, cursor, pageSize, ct,
            reserveProviderReason: true).ConfigureAwait(false);

        if (page.IsFailure) return page.Error;

        Dictionary<(string Provider, string Model), Result<GuildSpendRefreshCurrentness>> observations = [];

        ImmutableArray<GuildSpendEffectDto>.Builder items = ImmutableArray.CreateBuilder<GuildSpendEffectDto>(page.Value.Items.Length);

        List<(Guid EffectId, GuildSpendRefreshCurrentness Currentness)> candidates = [];

        HashSet<Guid> candidateIds = [];

        foreach (GuildSpendEffectDto effect in page.Value.Items)
        {
            var prepared = await PrepareEligibilityAsync(caller, effect, observations, ct).ConfigureAwait(false);

            items.Add(prepared.Effect);

            if (prepared.Currentness is not null)
            {
                candidates.Add((effect.EffectId, prepared.Currentness));

                candidateIds.Add(effect.EffectId);
            }
        }

        if (candidates.Count > 0)
        {
            Result<Dictionary<Guid, Result>> checkedBatch = await quests.CheckProviderRefreshEligibilityBatchAsync(caller, questId,
                candidates, ct).ConfigureAwait(false);

            for (int index = 0; index < items.Count; index++)
                if (candidateIds.Contains(items[index].EffectId))
                    items[index] = ApplyEligibilityResult(items[index], checkedBatch);
        }

        return page.Value with { Items = items.MoveToImmutable() };
    }

    private static GuildSpendEffectDto ApplyEligibilityResult(GuildSpendEffectDto effect, Result<Dictionary<Guid, Result>> checkedBatch)
    {
        if (checkedBatch.IsFailure || !checkedBatch.Value.TryGetValue(effect.EffectId, out Result? result) || result is null)
            return effect with { ProviderIneligibility = "sourceUnavailable" };

        string? reason = result.IsSuccess ? null : result.Error.Code switch
        {
            ErrorCodes.Guild.SourceExpired => "sourceExpired",
            ErrorCodes.Guild.InitialEvidenceUnavailable => "initialEvidenceUnavailable",
            _ => "sourceUnavailable",
        };

        return effect with { ProviderIneligibility = reason };
    }

    private async Task<(GuildSpendEffectDto Effect, GuildSpendRefreshCurrentness? Currentness)> PrepareEligibilityAsync(GuildCallerContext caller, GuildSpendEffectDto effect,
        Dictionary<(string Provider, string Model), Result<GuildSpendRefreshCurrentness>> observations, CancellationToken ct)
    {
        if (effect.Kind == GuildSpendEffectKind.Remote) return (effect with { ProviderIneligibility = "remoteEffect" }, null);

        if (effect.OperatorIneligibility is "sourceExpired" or "initialEvidenceUnavailable")
            return (effect with { ProviderIneligibility = effect.OperatorIneligibility }, null);

        Result<GuildSpendRefreshPreparation> candidate = await quests.ReadProviderEligibilitySelectionAsync(caller, effect.QuestId,
            effect.EffectId, ct).ConfigureAwait(false);

        if (candidate.IsFailure || candidate.Value.Provider is not { } provider || candidate.Value.Model is not { } model)
            return (effect with { ProviderIneligibility = "sourceUnavailable" }, null);

        if (configuration.IsExplicitlyDisabled(provider)) return (effect with { ProviderIneligibility = "reconciliationDisabled" }, null);

        Result<GuildSpendRefreshPreparation> source = await quests.SelectProviderSpendRefreshSourceAsync(caller, effect.QuestId,
            effect.EffectId, ct).ConfigureAwait(false);

        if (source.IsFailure || source.Value.Provider != provider || source.Value.Model != model)
            return (effect with { ProviderIneligibility = "sourceUnavailable" }, null);

        var key = (provider, model);

        if (!observations.TryGetValue(key, out Result<GuildSpendRefreshCurrentness>? observed))
        {
            try
            {
                observed = await GuildSpendRefreshCurrentness.CaptureAsync(configuration, credentials, provider, model, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                ct.ThrowIfCancellationRequested();

                observed = Unavailable;
            }

            observations.Add(key, observed);
        }

        if (observed is null || observed.IsFailure) return (effect with { ProviderIneligibility = "sourceUnavailable" }, null);

        return (effect, observed.Value);
    }

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

            entry.BindOperation(operationId);

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

    internal async Task<Result<bool>> RetryAsync(GuildCallerContext caller, LongRunningOperation operation, CancellationToken ct)
    {
        if (operation.Kind != LongRunningOperationKinds.GuildSpendRefresh
            || operation.RecoveryPolicy != LongRunningOperationRecoveryPolicy.ReconcileAndComplete
            || operation.CheckpointVersion != 1 || operation.CheckpointPayload is not { Length: > 0 and <= 2048 } payload
            || ownership.IsClaimed(operation.Id)) return Unavailable;

        GuildSpendRefreshCheckpoint? checkpoint;

        try { checkpoint = JsonSerializer.Deserialize(payload, GuildSpendRefreshCheckpointJsonContext.Default.GuildSpendRefreshCheckpoint); }
        catch (JsonException) { return Unavailable; }

        if (checkpoint is null || checkpoint.Version != 1 || checkpoint.Phase != "admitted" || checkpoint.OperationId != operation.Id
            || checkpoint.InstallationGeneration != caller.InstallationGeneration || checkpoint.PrincipalId != caller.PrincipalId
            || !payload.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(checkpoint, GuildSpendRefreshCheckpointJsonContext.Default.GuildSpendRefreshCheckpoint)))
            return Unavailable;

        Result<GuildSpendRefreshPreparation> existing = await quests.PrepareProviderSpendRefreshAsync(caller, checkpoint.QuestId,
            checkpoint.EffectId, checkpoint.RefreshId, ct).ConfigureAwait(false);

        if (existing.IsFailure) return existing.Error;

        if (existing.Value.OperationId != operation.Id || existing.Value.Receipt is not null) return Unavailable;

        Result<GuildSpendRefreshPreparation> selected = await quests.SelectProviderSpendRefreshSourceAsync(caller,
            checkpoint.QuestId, checkpoint.EffectId, ct).ConfigureAwait(false);

        if (selected.IsFailure) return selected.Error;

        if (selected.Value.RootQuestId != checkpoint.RootQuestId
            || selected.Value.Provider is not { } provider || selected.Value.Model is not { } model) return Unavailable;

        Result<GuildSpendRefreshCurrentness> current = await GuildSpendRefreshCurrentness.CaptureAsync(configuration, credentials,
            provider, model, ct).ConfigureAwait(false);

        if (current.IsFailure) return current.Error;

        if (!tracker.TryEnter(checkpoint.RootQuestId, out GuildSpendRefreshTracker.Entry? entry) || entry is null) return Unavailable;

        using (entry)
        using (CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, entry.Stop))
            return await quests.RetryProviderSpendRefreshAsync(caller, checkpoint.QuestId, checkpoint.EffectId, checkpoint.RefreshId,
                operation, current.Value, ownership, entry, lifetime.Token).ConfigureAwait(false);
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

        if (operation.State == LongRunningOperationState.Cancelling && !string.IsNullOrWhiteSpace(operation.LeaseOwner)
            && operation.LeaseExpiresAt > time.GetUtcNow()) return LongRunningOperationRecoveryResult.Abandoned(ErrorCodes.Guild.RefreshCancelled);

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

        entry.BindOperation(operation.Id);

        using (entry)
        using (CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, entry.Stop))
        {
            Result<GuildSpendRefreshResult> result = await RunClaimedAsync(caller, checkpoint.QuestId, checkpoint.EffectId,
                checkpoint.RefreshId, operation.Id, provider, model, before.Value, entry, lifetime.Token, operation.LeaseOwner).ConfigureAwait(false);

            if (result.IsSuccess && result.Value.Receipt is not null) return LongRunningOperationRecoveryResult.Completed();

            LongRunningOperation? latest = await operations.GetAsync(operation.Id, CancellationToken.None).ConfigureAwait(false);

            if (latest is not null && latest.State == LongRunningOperationState.Cancelling && latest.LeaseOwner == operation.LeaseOwner
                && latest.LeaseExpiresAt > time.GetUtcNow()) return LongRunningOperationRecoveryResult.Abandoned(ErrorCodes.Guild.RefreshCancelled);

            return LongRunningOperationRecoveryResult.RequiresAttention(result.IsFailure ? result.Error.Code : result.Value.ErrorCode ?? ErrorCodes.Guild.Unavailable);
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
            LongRunningOperationState.Abandoned when operation.TerminalErrorCode == ErrorCodes.Guild.RefreshCancelled =>
                new GuildSpendRefreshResult(operationId, null, false, false, Cancelled: true),
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

        if (latest is not null && operations is IGuildRefreshOwnedSettlement settlement)
            _ = await settlement.TrySettleGuildRefreshAsync(operationId, latest.Revision, owner, LongRunningOperationState.Completed,
                null, time, CancellationToken.None).ConfigureAwait(false);

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

        if (operations is IGuildRefreshOwnedSettlement settlement)
        {
            LongRunningOperationState? settled = await settlement.TrySettleGuildRefreshAsync(operationId, latest.Revision, owner,
                LongRunningOperationState.ReconciliationRequired, error.Code, time, CancellationToken.None).ConfigureAwait(false);

            if (settled == LongRunningOperationState.Abandoned)
                return new GuildSpendRefreshResult(operationId, null, false, false, Cancelled: true);

            if (settled == LongRunningOperationState.ReconciliationRequired)
                return new GuildSpendRefreshResult(operationId, null, false, false, true, error.Code);
        }

        return new GuildSpendRefreshResult(operationId, null, false, true);
    }

    private static Error Unavailable => new(ErrorCodes.Guild.Unavailable, "Provider spend refresh is unavailable.");
}
