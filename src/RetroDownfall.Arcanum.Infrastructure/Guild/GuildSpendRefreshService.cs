using System.Text.Json;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Operations;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Reconciles saved spend-refresh evidence under the generic operation recovery lease.</summary>
internal sealed class GuildSpendRefreshService(GuildQuestStore quests, LongRunningOperationOwnership ownership, TimeProvider time)
{
    internal async Task<LongRunningOperationRecoveryResult> RecoverClaimedAsync(
        GuildCallerContext caller, LongRunningOperation operation, CancellationToken ct)
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

        if (operation.State is not (LongRunningOperationState.Running or LongRunningOperationState.Waiting or LongRunningOperationState.Cancelling)
            || string.IsNullOrWhiteSpace(operation.LeaseOwner) || operation.LeaseExpiresAt <= time.GetUtcNow())
        {
            return LongRunningOperationRecoveryResult.RequiresAttention(ErrorCodes.Guild.Unavailable);
        }

        Result<bool> receipt = await quests.ReadHistoricalSpendRefreshAsync(caller, checkpoint, ct).ConfigureAwait(false);

        if (receipt.IsFailure) return LongRunningOperationRecoveryResult.RequiresAttention(receipt.Error.Code);

        if (receipt.Value) return LongRunningOperationRecoveryResult.Completed();

        return LongRunningOperationRecoveryResult.Abandoned(operation.State == LongRunningOperationState.Cancelling
            ? ErrorCodes.Guild.RefreshCancelled
            : ErrorCodes.Guild.SpendRefreshRetired);
    }
}
