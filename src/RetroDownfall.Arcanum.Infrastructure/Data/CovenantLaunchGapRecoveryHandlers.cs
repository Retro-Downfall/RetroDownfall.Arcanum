using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// The stopped-host CLI's recovery-only view of the two durable erasure launches bootstrap may adopt.
/// </summary>
/// <remarks>
/// The CLI must be able to finish an exact launch-gap owner before publishing Grimoire readiness,
/// but it must not gain the host's general retention service or background sweep. This facade keeps
/// those capabilities out of the container while delegating the two admitted launch versions to the
/// same production recovery implementation the host uses.
/// </remarks>
internal sealed class CovenantLaunchGapRecovery
{
    private readonly DataRetentionService _retention;

    public CovenantLaunchGapRecovery(
        ArcanumDbContext db,
        IOptionsMonitor<ArcanumSettings> settings,
        ILongRunningOperationStore operations,
        TimeProvider timeProvider,
        ILogger<DataRetentionService> logger,
        ICovenantLabeledArtifactGuard labeledArtifactGuard,
        CovenantErasureCoordinator covenantErasureCoordinator)
    {
        _retention = new DataRetentionService(
            db,
            settings,
            operations,
            timeProvider,
            logger,
            labeledArtifactGuard,
            covenantErasureCoordinator: covenantErasureCoordinator);
    }

    public Task<LongRunningOperationRecoveryResult> RecoverMutationAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken) =>
        operation.CheckpointVersion == CovenantOfflineTransitionLaunchV4.CurrentVersion
            ? _retention.RecoverMutationAsync(operation, cancellationToken)
            : Task.FromResult(LongRunningOperationRecoveryResult.RequiresAttention(
                LongRunningOperationErrorCodes.UnsupportedCheckpointVersion));

    public Task<LongRunningOperationRecoveryResult> RecoverFactoryResetAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken) =>
        operation.CheckpointVersion == DataRetentionFactoryTransitionLaunchV2.CurrentVersion
            ? _retention.RecoverFactoryResetAsync(operation, cancellationToken)
            : Task.FromResult(LongRunningOperationRecoveryResult.RequiresAttention(
                LongRunningOperationErrorCodes.UnsupportedCheckpointVersion));
}

/// <summary>Finishes only an adopted Covenant-reset launch gap.</summary>
internal sealed class CovenantLaunchGapMutationRecoveryHandler(
    CovenantLaunchGapRecovery recovery) : ILongRunningOperationRecoveryHandler
{
    public string Kind => LongRunningOperationKinds.DataRetentionMutation;

    public int SupportedCheckpointVersion => CovenantOfflineTransitionLaunchV4.CurrentVersion;

    public Task<LongRunningOperationRecoveryResult> RecoverAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken) =>
        recovery.RecoverMutationAsync(operation, cancellationToken);
}

/// <summary>Finishes only an adopted healthy-catalog factory-erasure launch gap.</summary>
internal sealed class CovenantLaunchGapFactoryResetRecoveryHandler(
    CovenantLaunchGapRecovery recovery) : ILongRunningOperationRecoveryHandler
{
    public string Kind => LongRunningOperationKinds.DataRetentionFactoryReset;

    public int SupportedCheckpointVersion => DataRetentionFactoryTransitionLaunchV2.CurrentVersion;

    public Task<LongRunningOperationRecoveryResult> RecoverAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken) =>
        recovery.RecoverFactoryResetAsync(operation, cancellationToken);
}
