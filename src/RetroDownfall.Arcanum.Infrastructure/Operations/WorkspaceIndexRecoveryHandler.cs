using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Operations;

namespace RetroDownfall.Arcanum.Infrastructure.Operations;

/// <summary>
/// Closes a workspace-index operation that died mid-pass.
/// </summary>
/// <remarks>
/// Indexing is idempotent by file identity and content hash, and the persisted chunk rows are the
/// authority — a half-finished pass leaves correct rows for the files it reached and nothing for the
/// rest, so there is no partial state to unwind. Recovery deliberately does not re-enumerate: that
/// would duplicate the work the ordinary background tick is about to do anyway, on a startup path
/// that has to stay bounded. Closing the row is the whole job; re-registration happens the next time
/// a turn names the workspace.
/// </remarks>
internal sealed class WorkspaceIndexRecoveryHandler(
    ILogger<WorkspaceIndexRecoveryHandler> logger) : ILongRunningOperationRecoveryHandler
{
    public string Kind => LongRunningOperationKinds.WorkspaceIndex;

    public int SupportedCheckpointVersion => 0;

    public Task<LongRunningOperationRecoveryResult> RecoverAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        logger.LogInformation(
            "Workspace-index recovery: operation {OperationId} is closed; already-indexed rows remain the "
            + "authority and the next background tick re-enumerates.",
            operation.Id);

        return Task.FromResult(LongRunningOperationRecoveryResult.Completed());
    }
}
