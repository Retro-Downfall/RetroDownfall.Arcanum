using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Operations;

/// <summary>
/// Reconciles an attachment promotion interrupted between its temp bytes and its durable row
/// (#40, requirement 6).
/// </summary>
/// <remarks>
/// The reconciliation itself already exists and is the same routine startup and the periodic sweep
/// use: stale pending rows are collected, bound orphans whose session or file is gone are removed,
/// and unreferenced temp/final files are deleted, all serialized behind the per-session and pending
/// gates so it cannot race a live promote or fork. That makes it safe to call repeatedly, which is
/// exactly what a recovery handler needs — this handler adds the durable-ledger entry point, not a
/// second implementation that could disagree with the first.
///
/// <c>pendingOlderThan</c> is zero here on purpose: an operation only reaches recovery once its
/// lease has already expired, so its pending rows are stale by definition and waiting longer would
/// leave half-written bytes on disk until the next sweep.
/// </remarks>
internal sealed class AttachmentPromotionRecoveryHandler(
    ISessionAttachmentStore attachments,
    ILogger<AttachmentPromotionRecoveryHandler> logger) : ILongRunningOperationRecoveryHandler
{
    public string Kind => LongRunningOperationKinds.AttachmentPromotion;

    public int SupportedCheckpointVersion => 0;

    public async Task<LongRunningOperationRecoveryResult> RecoverAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await attachments.ReconcileAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Attachment recovery: operation {OperationId} reconciled pending rows, orphaned bindings, and "
            + "unreferenced blobs. Valid prior versions are untouched and no duplicate version was created.",
            operation.Id);

        return LongRunningOperationRecoveryResult.Completed();
    }
}
