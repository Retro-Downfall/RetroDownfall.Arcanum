using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

namespace RetroDownfall.Arcanum.Infrastructure.Operations;

internal enum LongRunningRecoveryAdmissionKind : byte
{
    OrdinaryDbOnly = 1,
    OrdinaryExternalEffect = 2,
    OwnerBoundOffline = 3,
    OwnerBoundAwaitingExactOwner = 4,
    UnsupportedCheckpointVersion = 5,
}

internal readonly record struct LongRunningRecoveryAdmissionDecision(
    LongRunningRecoveryAdmissionKind Kind,
    string? ErrorCode);

internal static class LongRunningOperationRecoveryAdmission
{
    internal static LongRunningRecoveryAdmissionDecision Classify(
        LongRunningOperation operation,
        LongRunningRecoveryOwnerEvidence? ownerEvidence)
    {
        ArgumentNullException.ThrowIfNull(operation);

        LongRunningRecoveryAdmissionKind kind = (operation.Kind, operation.CheckpointVersion) switch
        {
            (LongRunningOperationKinds.InferenceRun, 0) => LongRunningRecoveryAdmissionKind.OrdinaryDbOnly,
            (LongRunningOperationKinds.Subagent, 0) => LongRunningRecoveryAdmissionKind.OrdinaryDbOnly,
            (LongRunningOperationKinds.BudgetReservation, 0) => LongRunningRecoveryAdmissionKind.OrdinaryDbOnly,
            (LongRunningOperationKinds.Batch, 0) => LongRunningRecoveryAdmissionKind.OrdinaryExternalEffect,
            (LongRunningOperationKinds.Apprentice, 0 or 1) => LongRunningRecoveryAdmissionKind.OrdinaryDbOnly,
            (LongRunningOperationKinds.AttachmentPromotion, 0) => LongRunningRecoveryAdmissionKind.OrdinaryExternalEffect,
            (LongRunningOperationKinds.WorkspaceIndex, 0) => LongRunningRecoveryAdmissionKind.OrdinaryDbOnly,
            (LongRunningOperationKinds.IdempotencyClaim, 0) => LongRunningRecoveryAdmissionKind.OrdinaryDbOnly,
            (LongRunningOperationKinds.BlobEncryptionMigration, 0 or 1) => LongRunningRecoveryAdmissionKind.OrdinaryExternalEffect,
            (LongRunningOperationKinds.BlobEncryptionKeyRotation, 0 or 1) => LongRunningRecoveryAdmissionKind.OrdinaryExternalEffect,
            (LongRunningOperationKinds.BackupCreate, 0) => LongRunningRecoveryAdmissionKind.OrdinaryDbOnly,
            (LongRunningOperationKinds.BackupCreate, 2) => LongRunningRecoveryAdmissionKind.OrdinaryExternalEffect,
            (LongRunningOperationKinds.DataRetentionPrune, 0 or 2) => LongRunningRecoveryAdmissionKind.OrdinaryExternalEffect,
            (LongRunningOperationKinds.DataRetentionMutation, 0) => LongRunningRecoveryAdmissionKind.OrdinaryDbOnly,
            (LongRunningOperationKinds.DataRetentionMutation, 2) => LongRunningRecoveryAdmissionKind.OrdinaryExternalEffect,
            (LongRunningOperationKinds.DataRetentionMutation, 4) => OwnerBoundKind(operation, ownerEvidence),
            (LongRunningOperationKinds.DataRetentionFactoryReset, 0) => LongRunningRecoveryAdmissionKind.OrdinaryExternalEffect,
            (LongRunningOperationKinds.DataRetentionFactoryReset, 2) => OwnerBoundKind(operation, ownerEvidence),
            (LongRunningOperationKinds.CovenantIndexRebuild, 1) => LongRunningRecoveryAdmissionKind.OrdinaryDbOnly,
            (LongRunningOperationKinds.CovenantFamilyReinitialize, 1) => LongRunningRecoveryAdmissionKind.OrdinaryDbOnly,
            (LongRunningOperationKinds.A2AInboundSending, 0 or 1) => LongRunningRecoveryAdmissionKind.OrdinaryDbOnly,
            (LongRunningOperationKinds.A2AOutboundSending, 0) => LongRunningRecoveryAdmissionKind.OrdinaryDbOnly,
            (LongRunningOperationKinds.A2AOutboundSending, 1) => LongRunningRecoveryAdmissionKind.OrdinaryExternalEffect,
            _ => LongRunningRecoveryAdmissionKind.UnsupportedCheckpointVersion,
        };

        return new LongRunningRecoveryAdmissionDecision(
            kind,
            kind is LongRunningRecoveryAdmissionKind.UnsupportedCheckpointVersion
                ? LongRunningOperationErrorCodes.UnsupportedCheckpointVersion
                : null);
    }

    private static LongRunningRecoveryAdmissionKind OwnerBoundKind(
        LongRunningOperation operation,
        LongRunningRecoveryOwnerEvidence? evidence)
    {
        if (evidence is null
            || evidence.ExpectedOperation.OperationId != operation.Id
            || !string.Equals(evidence.ExpectedOperation.Kind, operation.Kind, StringComparison.Ordinal)
            || evidence.ExpectedOperation.CheckpointVersion != operation.CheckpointVersion)
        {
            return LongRunningRecoveryAdmissionKind.OwnerBoundAwaitingExactOwner;
        }

        var launch = GrimoireOfflineTransitionLaunch.FromCommittedCheckpoint(
            operation.CheckpointVersion,
            operation.CheckpointPayload ?? []);

        if (launch.IsFailure
            || launch.Value.OperationId != operation.Id
            || !string.Equals(launch.Value.OperationKind, operation.Kind, StringComparison.Ordinal)
            || launch.Value.Operation != evidence.Owner.Operation
            || launch.Value.EffectDigest != evidence.Owner.EffectDigest
            || evidence.Owner.OperationId != operation.Id
            || !ExpectedExclusiveOperation(operation.Kind, evidence.Owner.Operation))
        {
            return LongRunningRecoveryAdmissionKind.OwnerBoundAwaitingExactOwner;
        }

        return LongRunningRecoveryAdmissionKind.OwnerBoundOffline;
    }

    private static bool ExpectedExclusiveOperation(
        string kind,
        CovenantExclusiveOperation operation) =>
        kind switch
        {
            LongRunningOperationKinds.DataRetentionMutation =>
                operation is CovenantExclusiveOperation.CovenantReset,
            LongRunningOperationKinds.DataRetentionFactoryReset =>
                operation is CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
            _ => false,
        };
}
