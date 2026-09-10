using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal enum DataRetentionHostedSweepDisposition : byte
{
    Concluded = 1,
    DeferredForMaintenance = 2,
}

internal sealed record DataRetentionHostedSweepContinuation(
    Guid OperationId,
    string OwnerId,
    Guid OwnershipToken);

internal sealed record DataRetentionHostedSweepOutcome(
    DataRetentionHostedSweepDisposition Disposition,
    DataRetentionHostedSweepContinuation? Continuation,
    Result<DataRetentionApplyResult>? Result);

/// <summary>
/// The host-only retention entry point that accepts ordinary-work authority and can retain its exact
/// durable operation across a maintenance interruption.
/// </summary>
internal interface IDataRetentionHostedSweep
{
    Task<DataRetentionHostedSweepOutcome> ApplyOrResumeHostedPruneAsync(
        DataRetentionHostedSweepContinuation? continuation,
        IGrimoireWorkLease workLease,
        CancellationToken cancellationToken);
}
