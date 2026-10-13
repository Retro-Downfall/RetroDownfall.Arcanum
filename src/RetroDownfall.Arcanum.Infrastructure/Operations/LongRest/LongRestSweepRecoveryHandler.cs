using RetroDownfall.Arcanum.Core.Operations;

namespace RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;

internal sealed class LongRestSweepRecoveryHandler(LongRestSweepCoordinator coordinator) : ILongRunningOperationRecoveryHandler
{
    public string Kind => LongRunningOperationKinds.LongRestSweep;

    public int SupportedCheckpointVersion => 1;

    public Task<LongRunningOperationRecoveryResult> RecoverAsync(LongRunningOperation operation, CancellationToken cancellationToken)
    {
        if (operation.Kind != Kind || operation.CheckpointVersion is < 0 or > 1)
        {
            return Task.FromResult(LongRunningOperationRecoveryResult.RequiresAttention(LongRunningOperationErrorCodes.UnsupportedCheckpointVersion));
        }

        if (operation.State == LongRunningOperationState.Cancelling)
        {
            return Task.FromResult(LongRunningOperationRecoveryResult.Abandoned("long_rest.cancelled"));
        }

        if (operation.CheckpointVersion == 0)
        {
            return Task.FromResult(LongRunningOperationRecoveryResult.Abandoned("long_rest.not_started"));
        }

        var checkpoint = LongRestSweepCheckpointCodec.Decode(operation.Id, operation.CheckpointPayload ?? []);

        if (checkpoint.IsFailure)
        {
            return Task.FromResult(LongRunningOperationRecoveryResult.RequiresAttention(checkpoint.Error.Code));
        }

        if (checkpoint.Value.ProtectedPartitionDigest is not null)
        {
            return Task.FromResult(LongRunningOperationRecoveryResult.RequiresAttention(LongRestSweepCoordinator.AuthorizedRequestRequired));
        }

        return coordinator.RecoverAsync(operation, cancellationToken);
    }
}
