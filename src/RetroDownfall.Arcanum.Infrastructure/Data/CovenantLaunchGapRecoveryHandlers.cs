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
        ICovenantLabeledArtifactTransactionGuard labeledArtifactGuard,
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

    public async Task<LongRunningOperationRecoveryResult> RecoverMutationAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken) =>
        operation.CheckpointVersion == CovenantOfflineTransitionLaunchV4.CurrentVersion
            ? await _retention.RecoverMutationAsync(operation, cancellationToken).ConfigureAwait(false)
            : LongRunningOperationRecoveryResult.RequiresAttention(
                LongRunningOperationErrorCodes.UnsupportedCheckpointVersion);

    public async Task<LongRunningOperationRecoveryResult> RecoverMutationAuthenticatedAsync(
        LongRunningOperation operation,
        CovenantErasureCoordinator.AuthenticatedCovenantErasureRecoveryAdmission admission,
        CancellationToken cancellationToken) =>
        operation.CheckpointVersion == CovenantOfflineTransitionLaunchV4.CurrentVersion
            ? await _retention.RecoverMutationAuthenticatedAsync(operation, admission, cancellationToken)
                .ConfigureAwait(false)
            : LongRunningOperationRecoveryResult.RequiresAttention(
                LongRunningOperationErrorCodes.UnsupportedCheckpointVersion);

    public async Task<LongRunningOperationRecoveryResult> RecoverFactoryResetAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken) =>
        operation.CheckpointVersion == DataRetentionFactoryTransitionLaunchV2.CurrentVersion
            ? await _retention.RecoverFactoryResetAsync(operation, cancellationToken).ConfigureAwait(false)
            : LongRunningOperationRecoveryResult.RequiresAttention(
                LongRunningOperationErrorCodes.UnsupportedCheckpointVersion);

    public async Task<LongRunningOperationRecoveryResult> RecoverFactoryResetAuthenticatedAsync(
        LongRunningOperation operation,
        CovenantErasureCoordinator.AuthenticatedCovenantErasureRecoveryAdmission admission,
        CancellationToken cancellationToken) =>
        operation.CheckpointVersion == DataRetentionFactoryTransitionLaunchV2.CurrentVersion
            ? await _retention.RecoverFactoryResetAuthenticatedAsync(
                    operation,
                    admission,
                    cancellationToken)
                .ConfigureAwait(false)
            : LongRunningOperationRecoveryResult.RequiresAttention(
                LongRunningOperationErrorCodes.UnsupportedCheckpointVersion);
}

/// <summary>Finishes only an adopted Covenant-reset launch gap.</summary>
internal sealed class CovenantLaunchGapMutationRecoveryHandler(
    CovenantLaunchGapRecovery recovery) : IAuthenticatedCovenantErasureRecoveryHandler
{
    public string Kind => LongRunningOperationKinds.DataRetentionMutation;

    public int SupportedCheckpointVersion => CovenantOfflineTransitionLaunchV4.CurrentVersion;

    public async Task<LongRunningOperationRecoveryResult> RecoverAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken) =>
        await recovery.RecoverMutationAsync(operation, cancellationToken).ConfigureAwait(false);

    public async Task<LongRunningOperationRecoveryResult> RecoverAuthenticatedAsync(
        LongRunningOperation operation,
        CovenantErasureCoordinator.AuthenticatedCovenantErasureRecoveryAdmission admission,
        CancellationToken cancellationToken) =>
        await recovery.RecoverMutationAuthenticatedAsync(operation, admission, cancellationToken)
            .ConfigureAwait(false);
}

/// <summary>Finishes only an adopted healthy-catalog factory-erasure launch gap.</summary>
internal sealed class CovenantLaunchGapFactoryResetRecoveryHandler(
    CovenantLaunchGapRecovery recovery) : IAuthenticatedCovenantErasureRecoveryHandler
{
    public string Kind => LongRunningOperationKinds.DataRetentionFactoryReset;

    public int SupportedCheckpointVersion => DataRetentionFactoryTransitionLaunchV2.CurrentVersion;

    public async Task<LongRunningOperationRecoveryResult> RecoverAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken) =>
        await recovery.RecoverFactoryResetAsync(operation, cancellationToken).ConfigureAwait(false);

    public async Task<LongRunningOperationRecoveryResult> RecoverAuthenticatedAsync(
        LongRunningOperation operation,
        CovenantErasureCoordinator.AuthenticatedCovenantErasureRecoveryAdmission admission,
        CancellationToken cancellationToken) =>
        await recovery.RecoverFactoryResetAuthenticatedAsync(operation, admission, cancellationToken)
            .ConfigureAwait(false);
}
