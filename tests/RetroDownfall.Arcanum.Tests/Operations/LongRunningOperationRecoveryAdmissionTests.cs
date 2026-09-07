using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Operations;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed class LongRunningOperationRecoveryAdmissionTests
{
    public static TheoryData<string, int, object> CompleteMatrix => new()
    {
        { LongRunningOperationKinds.InferenceRun, 0, LongRunningRecoveryAdmissionKind.OrdinaryDbOnly },
        { LongRunningOperationKinds.Subagent, 0, LongRunningRecoveryAdmissionKind.OrdinaryDbOnly },
        { LongRunningOperationKinds.BudgetReservation, 0, LongRunningRecoveryAdmissionKind.OrdinaryDbOnly },
        { LongRunningOperationKinds.Batch, 0, LongRunningRecoveryAdmissionKind.OrdinaryExternalEffect },
        { LongRunningOperationKinds.Apprentice, 0, LongRunningRecoveryAdmissionKind.OrdinaryDbOnly },
        { LongRunningOperationKinds.Apprentice, 1, LongRunningRecoveryAdmissionKind.OrdinaryDbOnly },
        { LongRunningOperationKinds.AttachmentPromotion, 0, LongRunningRecoveryAdmissionKind.OrdinaryExternalEffect },
        { LongRunningOperationKinds.WorkspaceIndex, 0, LongRunningRecoveryAdmissionKind.OrdinaryDbOnly },
        { LongRunningOperationKinds.IdempotencyClaim, 0, LongRunningRecoveryAdmissionKind.OrdinaryDbOnly },
        { LongRunningOperationKinds.BlobEncryptionMigration, 0, LongRunningRecoveryAdmissionKind.OrdinaryExternalEffect },
        { LongRunningOperationKinds.BlobEncryptionMigration, 1, LongRunningRecoveryAdmissionKind.OrdinaryExternalEffect },
        { LongRunningOperationKinds.BlobEncryptionKeyRotation, 0, LongRunningRecoveryAdmissionKind.OrdinaryExternalEffect },
        { LongRunningOperationKinds.BlobEncryptionKeyRotation, 1, LongRunningRecoveryAdmissionKind.OrdinaryExternalEffect },
        { LongRunningOperationKinds.BackupCreate, 0, LongRunningRecoveryAdmissionKind.OrdinaryDbOnly },
        { LongRunningOperationKinds.BackupCreate, 1, LongRunningRecoveryAdmissionKind.UnsupportedCheckpointVersion },
        { LongRunningOperationKinds.BackupCreate, 2, LongRunningRecoveryAdmissionKind.OrdinaryExternalEffect },
        { LongRunningOperationKinds.DataRetentionPrune, 0, LongRunningRecoveryAdmissionKind.OrdinaryExternalEffect },
        { LongRunningOperationKinds.DataRetentionPrune, 1, LongRunningRecoveryAdmissionKind.UnsupportedCheckpointVersion },
        { LongRunningOperationKinds.DataRetentionPrune, 2, LongRunningRecoveryAdmissionKind.OrdinaryExternalEffect },
        { LongRunningOperationKinds.DataRetentionMutation, 0, LongRunningRecoveryAdmissionKind.OrdinaryDbOnly },
        { LongRunningOperationKinds.DataRetentionMutation, 1, LongRunningRecoveryAdmissionKind.UnsupportedCheckpointVersion },
        { LongRunningOperationKinds.DataRetentionMutation, 2, LongRunningRecoveryAdmissionKind.OrdinaryExternalEffect },
        { LongRunningOperationKinds.DataRetentionMutation, 3, LongRunningRecoveryAdmissionKind.UnsupportedCheckpointVersion },
        { LongRunningOperationKinds.DataRetentionMutation, 4, LongRunningRecoveryAdmissionKind.OwnerBoundAwaitingExactOwner },
        { LongRunningOperationKinds.DataRetentionFactoryReset, 0, LongRunningRecoveryAdmissionKind.OrdinaryExternalEffect },
        { LongRunningOperationKinds.DataRetentionFactoryReset, 1, LongRunningRecoveryAdmissionKind.UnsupportedCheckpointVersion },
        { LongRunningOperationKinds.DataRetentionFactoryReset, 2, LongRunningRecoveryAdmissionKind.OwnerBoundAwaitingExactOwner },
        { LongRunningOperationKinds.CovenantIndexRebuild, 1, LongRunningRecoveryAdmissionKind.OrdinaryDbOnly },
        { LongRunningOperationKinds.CovenantFamilyReinitialize, 1, LongRunningRecoveryAdmissionKind.OrdinaryDbOnly },
        { LongRunningOperationKinds.A2AInboundSending, 0, LongRunningRecoveryAdmissionKind.OrdinaryDbOnly },
        { LongRunningOperationKinds.A2AInboundSending, 1, LongRunningRecoveryAdmissionKind.OrdinaryDbOnly },
        { LongRunningOperationKinds.A2AOutboundSending, 0, LongRunningRecoveryAdmissionKind.OrdinaryDbOnly },
        { LongRunningOperationKinds.A2AOutboundSending, 1, LongRunningRecoveryAdmissionKind.OrdinaryExternalEffect },
    };

    [Theory]
    [MemberData(nameof(CompleteMatrix))]
    public void Classification_is_closed_over_every_supported_kind_and_version(
        string kind,
        int version,
        object expectedValue)
    {
        LongRunningRecoveryAdmissionKind expected = Assert.IsType<LongRunningRecoveryAdmissionKind>(expectedValue);

        LongRunningRecoveryAdmissionDecision actual = LongRunningOperationRecoveryAdmission.Classify(
            Operation(kind, version),
            ownerEvidence: null);

        Assert.Equal(expected, actual.Kind);
        Assert.Equal(
            expected is LongRunningRecoveryAdmissionKind.UnsupportedCheckpointVersion
                ? LongRunningOperationErrorCodes.UnsupportedCheckpointVersion
                : null,
            actual.ErrorCode);
    }

    [Fact]
    public void Every_out_of_window_or_unregistered_tuple_is_unsupported()
    {
        foreach (LongRunningOperationRecoveryDescriptor descriptor in
            LongRunningOperationRecoveryRegistry.Descriptors.Values)
        {
            AssertUnsupported(descriptor.Kind, descriptor.MinCheckpointVersion - 1);
            AssertUnsupported(descriptor.Kind, descriptor.MaxCheckpointVersion + 1);
        }

        AssertUnsupported("future-unregistered-kind", 0);
    }

    [Fact]
    public void Every_registered_tuple_appears_exactly_once_in_the_closed_matrix()
    {
        var rows = CompleteMatrix
            .Select(values => (Kind: Assert.IsType<string>(values[0]), Version: Assert.IsType<int>(values[1])))
            .ToArray();

        Assert.Equal(rows.Length, rows.Distinct().Count());

        foreach (LongRunningOperationRecoveryDescriptor descriptor in
            LongRunningOperationRecoveryRegistry.Descriptors.Values)
        {
            Assert.All(
                Enumerable.Range(
                    descriptor.MinCheckpointVersion,
                    descriptor.MaxCheckpointVersion - descriptor.MinCheckpointVersion + 1),
                version => Assert.Contains((descriptor.Kind, version), rows));
        }
    }

    private static void AssertUnsupported(string kind, int version)
    {
        LongRunningRecoveryAdmissionDecision decision = LongRunningOperationRecoveryAdmission.Classify(
            Operation(kind, version),
            ownerEvidence: null);

        Assert.Equal(LongRunningRecoveryAdmissionKind.UnsupportedCheckpointVersion, decision.Kind);
        Assert.Equal(LongRunningOperationErrorCodes.UnsupportedCheckpointVersion, decision.ErrorCode);
    }

    private static LongRunningOperation Operation(string kind, int version) => new(
        Guid.NewGuid(),
        kind,
        LongRunningOperationState.Running,
        LongRunningOperationRecoveryRegistry.Find(kind)?.Policy
            ?? LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
        RootOperationId: null,
        ParentOperationId: null,
        SessionId: null,
        RunId: null,
        InferenceRunId: null,
        BudgetReservationId: null,
        IdempotencyClaimId: null,
        DateTimeOffset.UnixEpoch,
        StartedAt: DateTimeOffset.UnixEpoch,
        HeartbeatAt: DateTimeOffset.UnixEpoch,
        CompletedAt: null,
        LeaseOwner: "prior-owner",
        LeaseExpiresAt: DateTimeOffset.UnixEpoch,
        AttemptCount: 1,
        CheckpointVersion: version,
        CheckpointPayload: null,
        CheckpointReference: null,
        PublicSummary: "test",
        TerminalErrorCode: null,
        Revision: 7);
}
