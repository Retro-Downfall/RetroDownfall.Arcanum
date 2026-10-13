using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;

namespace RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;

/// <summary>
/// The Pure Park Handler. Durable recovery never runs a Long Rest sweep: it decodes the checkpoint and parks the row
/// under a code that names which authority class may readmit it. It has no dependencies, so recovery cannot reach a
/// provider, a store or the coordinator. The hosted worker readmits a parked clean sweep under its own Long Rest lease,
/// and a fresh Session request readmits a protected one.
/// </summary>
internal sealed class LongRestSweepRecoveryHandler : ILongRunningOperationRecoveryHandler
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
            return Task.FromResult(LongRunningOperationRecoveryResult.Abandoned(LongRestParkCodes.Cancelled));
        }

        if (operation.CheckpointVersion == 0)
        {
            return Task.FromResult(LongRunningOperationRecoveryResult.Abandoned(LongRestParkCodes.NotStarted));
        }

        var checkpoint = LongRestSweepCheckpointCodec.Decode(operation.Id, operation.CheckpointPayload ?? []);

        if (checkpoint.IsFailure)
        {
            return Task.FromResult(LongRunningOperationRecoveryResult.RequiresAttention(checkpoint.Error.Code));
        }

        if (checkpoint.Value.ProtectedPartitionDigest is not null)
        {
            return Task.FromResult(LongRunningOperationRecoveryResult.RequiresAttention(LongRestParkCodes.AuthorizedRequestRequired));
        }

        // A lease was just taken on the row to run this handler, so a retried row can only be told apart by the code
        // lease acquisition records when it adopts a Pending row that has already been attempted.
        string code = operation.State switch
        {
            LongRunningOperationState.Running or LongRunningOperationState.Waiting
                when operation.TerminalErrorCode == LongRestParkCodes.ReadmitRequested => LongRestParkCodes.ReadmitRequested,
            LongRunningOperationState.Running or LongRunningOperationState.Waiting => LongRestParkCodes.Interrupted,
            LongRunningOperationState.Pending => LongRestParkCodes.ReadmitRequested,
            _ => LongRunningOperationErrorCodes.RecoveryFailed,
        };

        return Task.FromResult(LongRunningOperationRecoveryResult.RequiresAttention(code));
    }
}
