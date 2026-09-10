using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The one projection both durable erasure launch shapes resolve into, and the identity rules it
/// refuses to bend.
/// </summary>
/// <remarks>
/// A Covenant reset and a healthy-catalog factory erasure are recorded in different journals because
/// their headers differ, but they resume from the same four facts. One projection is what lets a
/// single coordinator own both arms; two would let the reset arm and the factory arm disagree about
/// what a phase means, and the first divergence would be silent (§10.20.4).
/// </remarks>
public sealed class CovenantErasureCheckpointStateTests
{

    private static readonly Guid OperationId = new("55555555-5555-4555-8555-555555555555");

    private static readonly Guid OtherOperationId = new("66666666-6666-4666-8666-666666666666");

    private static readonly Guid SourceGeneration = new("77777777-7777-4777-8777-777777777777");

    private static readonly Guid TargetGeneration = new("88888888-8888-4888-8888-888888888888");

    private static CovenantOfflineTransitionEpochsV1 SourceEpochs => new(11, 22, 33);

    private static CovenantOfflineTransitionEpochsV1 TargetEpochs => new(12, 23, 34);

    /// <summary>
    /// A reset launch projects the first phase, whatever else the row holds.
    /// </summary>
    /// <remarks>
    /// The row used to carry a phase and the projection used to report it back. It no longer can: a
    /// launch records only what was committed to, and progress past that point lives in the
    /// authenticated journal. A projection that still reported a phase from this row would be
    /// answering for a surface it is no longer the authority for, and the answer would be stale
    /// exactly when it mattered — after a crash, where the journal has advanced and the row has not.
    /// The owner is still rebuilt from the launch and nothing else, because that is the field the
    /// exclusive gate is adopted against.
    /// </remarks>
    [Fact]
    public void A_reset_launch_projects_the_first_phase_and_the_owner_it_names()
    {

        Result<CovenantErasureCheckpointState> projected =
            CovenantErasureCheckpointState.FromMutationCheckpoint(
                OperationId,
                CovenantOfflineTransitionLaunchV4.CurrentVersion,
                ResetPayload(),
                out bool describesErasure);

        Assert.True(projected.IsSuccess);

        Assert.True(describesErasure);

        Assert.Equal(CovenantResetPhaseMachine.First, projected.Value.Phase);

        Assert.Equal(CovenantExclusiveOperation.CovenantReset, projected.Value.Operation);

        Assert.Equal(OperationId, projected.Value.Owner.OperationId);

        Assert.Equal(CovenantOperationGateFixture.Digest(7), projected.Value.Owner.EffectDigest);

    }

    /// <summary>
    /// The factory arm answers the same way, for the same reason.
    /// </summary>
    /// <remarks>
    /// The factory launch is a separate durable shape under a separate kind, so nothing but a test
    /// stops the two arms drifting into two different ideas of where a resumed erasure stands. Both
    /// must report the first phase and defer to the journal; an arm that kept reading a phase out of
    /// its row would resume from a step the other arm had already left behind.
    /// </remarks>
    [Fact]
    public void A_factory_launch_projects_the_first_phase_and_the_owner_it_names()
    {

        Result<CovenantErasureCheckpointState> projected =
            CovenantErasureCheckpointState.FromFactoryResetCheckpoint(
                OperationId,
                DataRetentionFactoryTransitionLaunchV2.CurrentVersion,
                FactoryPayload());

        Assert.True(projected.IsSuccess);

        Assert.Equal(CovenantResetPhaseMachine.First, projected.Value.Phase);

        Assert.Equal(CovenantExclusiveOperation.HealthyCatalogFactoryErasure, projected.Value.Operation);

        Assert.Equal(OperationId, projected.Value.Owner.OperationId);

    }

    [Fact]
    public void The_two_journals_agree_on_everything_except_the_operation_they_name()
    {

        CovenantErasureCheckpointState reset = CovenantErasureCheckpointState
            .FromMutationCheckpoint(
                OperationId,
                CovenantOfflineTransitionLaunchV4.CurrentVersion,
                ResetPayload(),
                out _)
            .Value;

        CovenantErasureCheckpointState factory = CovenantErasureCheckpointState
            .FromFactoryResetCheckpoint(
                OperationId,
                DataRetentionFactoryTransitionLaunchV2.CurrentVersion,
                FactoryPayload())
            .Value;

        Assert.Equal(reset.OperationId, factory.OperationId);

        Assert.Equal(reset.EffectDigest, factory.EffectDigest);

        Assert.Equal(reset.Phase, factory.Phase);

        // The operation code is the one field that must differ, and the owners must therefore never
        // be equal. A reset and a factory erasure over an identical inventory are different
        // destructive plans, and an owner shared between them would let one adopt the other's closed
        // scope.
        Assert.NotEqual(reset.Operation, factory.Operation);

        Assert.NotEqual(reset.Owner, factory.Owner);

    }

    [Fact]
    public void A_reset_journal_naming_another_operation_is_refused()
    {

        Result<CovenantErasureCheckpointState> projected =
            CovenantErasureCheckpointState.FromMutationCheckpoint(
                OtherOperationId,
                CovenantOfflineTransitionLaunchV4.CurrentVersion,
                ResetPayload(),
                out _);

        Assert.True(projected.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, projected.Error.Code);

    }

    [Fact]
    public void A_factory_journal_naming_another_operation_is_refused()
    {

        Result<CovenantErasureCheckpointState> projected =
            CovenantErasureCheckpointState.FromFactoryResetCheckpoint(
                OtherOperationId,
                DataRetentionFactoryTransitionLaunchV2.CurrentVersion,
                FactoryPayload());

        Assert.True(projected.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, projected.Error.Code);

    }

    /// <summary>
    /// A retention row filed at any version but the launch version is an ordinary mutation.
    /// </summary>
    /// <remarks>
    /// The row's own version, not the payload's contents, is what says whether a Covenant erasure ran
    /// here. Every other retention mutation closed nothing, so there is no exclusive scope to adopt
    /// and inventing one would close a scope this operation never opened. Asking the version rather
    /// than sniffing the bytes is the stronger rule: the payload below is a launch that would decode
    /// perfectly, and it is still an ordinary mutation, because a row that was never filed as a launch
    /// cannot become one by carrying launch-shaped bytes.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void A_retention_journal_filed_at_any_other_version_is_an_ordinary_mutation(int checkpointVersion)
    {

        Result<CovenantErasureCheckpointState> projected =
            CovenantErasureCheckpointState.FromMutationCheckpoint(
                OperationId,
                checkpointVersion,
                ResetPayload(),
                out bool describesErasure);

        Assert.True(projected.IsFailure);

        // The discriminator is what lets the recovery handler answer "ordinary reconciliation" here
        // and "an operator must look at this" for a launch it cannot resume. One code for both would
        // tell somebody to leave a stuck ordinary mutation alone forever.
        Assert.False(describesErasure);

    }

    [Fact]
    public void An_undecodable_payload_is_refused_rather_than_guessed()
    {

        byte[] payload = "{\"version\":4,\"operationKind\":\"data-retention-mutation\""u8.ToArray();

        Assert.True(
            CovenantErasureCheckpointState
                .FromMutationCheckpoint(
                    OperationId,
                    CovenantOfflineTransitionLaunchV4.CurrentVersion,
                    payload,
                    out bool describesErasure)
                .IsFailure);

        // A row filed at the launch version still counts as an erasure even when its bytes will not
        // decode. The version already said a destructive plan was committed here, and downgrading the
        // row to an ordinary mutation on a decode failure would reconcile something that may have a
        // half-erased family and a shut gate behind it.
        Assert.True(describesErasure);

        Assert.True(
            CovenantErasureCheckpointState
                .FromFactoryResetCheckpoint(
                    OperationId,
                    DataRetentionFactoryTransitionLaunchV2.CurrentVersion,
                    payload)
                .IsFailure);

    }

    [Fact]
    public void A_reset_journal_carrying_the_factory_operation_code_is_refused()
    {

        // The codec pins each arm to its own operation code. A reset journal that named the factory
        // erasure would rebuild an owner whose code and whose journal disagreed about what ran.
        byte[] payload = CovenantRecoveryCheckpointCodec.Encode(
            new CovenantOfflineTransitionLaunchV4(
                CovenantOfflineTransitionLaunchV4.CurrentVersion,
                OperationId,
                LongRunningOperationKinds.DataRetentionMutation,
                nameof(LongRunningOperationRecoveryPolicy.ReconcileAndComplete),
                CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
                CovenantRecoveryCheckpointCodec.EncodeEffectDigest(CovenantOperationGateFixture.Digest(7)),
                SourceGeneration,
                TargetGeneration,
                SourceEpochs,
                TargetEpochs,
                StartingRevision: 0));

        Assert.True(
            CovenantErasureCheckpointState
                .FromMutationCheckpoint(
                    OperationId,
                    CovenantOfflineTransitionLaunchV4.CurrentVersion,
                    payload,
                    out _)
                .IsFailure);

    }

    private static byte[] ResetPayload() =>
        CovenantRecoveryCheckpointCodec.Encode(
            new CovenantOfflineTransitionLaunchV4(
                CovenantOfflineTransitionLaunchV4.CurrentVersion,
                OperationId,
                LongRunningOperationKinds.DataRetentionMutation,
                nameof(LongRunningOperationRecoveryPolicy.ReconcileAndComplete),
                CovenantExclusiveOperation.CovenantReset,
                CovenantRecoveryCheckpointCodec.EncodeEffectDigest(CovenantOperationGateFixture.Digest(7)),
                SourceGeneration,
                TargetGeneration,
                SourceEpochs,
                TargetEpochs,
                StartingRevision: 0));

    private static byte[] FactoryPayload() =>
        CovenantRecoveryCheckpointCodec.Encode(
            new DataRetentionFactoryTransitionLaunchV2(
                DataRetentionFactoryTransitionLaunchV2.CurrentVersion,
                OperationId,
                LongRunningOperationKinds.DataRetentionFactoryReset,
                nameof(LongRunningOperationRecoveryPolicy.RestartIdempotently),
                CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
                CovenantRecoveryCheckpointCodec.EncodeEffectDigest(CovenantOperationGateFixture.Digest(7)),
                SourceGeneration,
                TargetGeneration,
                SourceEpochs,
                TargetEpochs,
                StartingRevision: 0));

}
