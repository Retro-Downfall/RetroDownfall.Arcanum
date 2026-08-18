using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The one projection both durable erasure checkpoint shapes resolve into, and the identity rules it
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

    [Theory]
    [InlineData(CovenantResetPhase.InventoryPrepared)]
    [InlineData(CovenantResetPhase.CanonicalApplied)]
    [InlineData(CovenantResetPhase.ManagedArtifactsProcessed)]
    [InlineData(CovenantResetPhase.HandlesClosed)]
    [InlineData(CovenantResetPhase.WalTruncated)]
    [InlineData(CovenantResetPhase.DatabaseCompacted)]
    [InlineData(CovenantResetPhase.AcceleratorInitialized)]
    [InlineData(CovenantResetPhase.FinalWalTruncated)]
    [InlineData(CovenantResetPhase.SidecarsVerified)]
    [InlineData(CovenantResetPhase.ReopenedVerified)]
    public void Every_declared_phase_round_trips_through_the_reset_journal(CovenantResetPhase phase)
    {

        Result<CovenantErasureCheckpointState> projected =
            CovenantErasureCheckpointState.FromMutationCheckpoint(
                OperationId,
                ResetPayload(phase),
                out bool describesErasure);

        Assert.True(projected.IsSuccess);

        Assert.True(describesErasure);

        Assert.Equal(phase, projected.Value.Phase);

        Assert.Equal(CovenantExclusiveOperation.CovenantReset, projected.Value.Operation);

        Assert.Equal(OperationId, projected.Value.Owner.OperationId);

        Assert.Equal(CovenantOperationGateFixture.Digest(7), projected.Value.Owner.EffectDigest);

    }

    [Theory]
    [InlineData(CovenantResetPhase.InventoryPrepared)]
    [InlineData(CovenantResetPhase.CanonicalApplied)]
    [InlineData(CovenantResetPhase.ManagedArtifactsProcessed)]
    [InlineData(CovenantResetPhase.HandlesClosed)]
    [InlineData(CovenantResetPhase.WalTruncated)]
    [InlineData(CovenantResetPhase.DatabaseCompacted)]
    [InlineData(CovenantResetPhase.AcceleratorInitialized)]
    [InlineData(CovenantResetPhase.FinalWalTruncated)]
    [InlineData(CovenantResetPhase.SidecarsVerified)]
    [InlineData(CovenantResetPhase.ReopenedVerified)]
    public void Every_declared_phase_round_trips_through_the_factory_journal(CovenantResetPhase phase)
    {

        Result<CovenantErasureCheckpointState> projected =
            CovenantErasureCheckpointState.FromFactoryResetCheckpoint(OperationId, FactoryPayload(phase));

        Assert.True(projected.IsSuccess);

        Assert.Equal(phase, projected.Value.Phase);

        Assert.Equal(CovenantExclusiveOperation.HealthyCatalogFactoryErasure, projected.Value.Operation);

        Assert.Equal(OperationId, projected.Value.Owner.OperationId);

    }

    [Fact]
    public void The_two_journals_agree_on_everything_except_the_operation_they_name()
    {

        CovenantErasureCheckpointState reset = CovenantErasureCheckpointState
            .FromMutationCheckpoint(OperationId, ResetPayload(CovenantResetPhase.DatabaseCompacted), out _)
            .Value;

        CovenantErasureCheckpointState factory = CovenantErasureCheckpointState
            .FromFactoryResetCheckpoint(OperationId, FactoryPayload(CovenantResetPhase.DatabaseCompacted))
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
                ResetPayload(CovenantResetPhase.CanonicalApplied),
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
                FactoryPayload(CovenantResetPhase.CanonicalApplied));

        Assert.True(projected.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, projected.Error.Code);

    }

    [Fact]
    public void A_retention_journal_with_no_Covenant_arm_is_refused()
    {

        // A version-3 row with no arm describes a mutation that closed nothing. There is no exclusive
        // scope to adopt, and inventing one would close a scope this operation never opened.
        byte[] payload = CovenantRecoveryCheckpointCodec.Encode(
            new DataRetentionMutationCheckpointV3(
                DataRetentionMutationCheckpointV3.CurrentVersion,
                Subtype: "delete-session",
                Target: "5",
                Covenant: null));

        Result<CovenantErasureCheckpointState> projected =
            CovenantErasureCheckpointState.FromMutationCheckpoint(
                OperationId,
                payload,
                out bool describesErasure);

        Assert.True(projected.IsFailure);

        // The discriminator is what lets the recovery handler answer "ordinary reconciliation" here
        // and "an operator must look at this" for an arm it cannot resume. One code for both would
        // tell somebody to leave a stuck ordinary mutation alone forever.
        Assert.False(describesErasure);

    }

    [Fact]
    public void An_undecodable_payload_is_refused_rather_than_guessed()
    {

        byte[] payload = "{\"version\":3,\"subtype\":\"reset-memory\""u8.ToArray();

        Assert.True(
            CovenantErasureCheckpointState
                .FromMutationCheckpoint(OperationId, payload, out bool describesErasure)
                .IsFailure);

        // Unknown counts as an erasure. A payload that would not decode cannot say whether it closed
        // admission, and calling it an ordinary mutation would reconcile a row that may have a
        // half-erased family and a shut gate behind it.
        Assert.True(describesErasure);

        Assert.True(CovenantErasureCheckpointState.FromFactoryResetCheckpoint(OperationId, payload).IsFailure);

    }

    [Fact]
    public void A_reset_journal_carrying_the_factory_operation_code_is_refused()
    {

        // The codec pins each arm to its own operation code. A reset journal that named the factory
        // erasure would rebuild an owner whose code and whose journal disagreed about what ran.
        byte[] payload = CovenantRecoveryCheckpointCodec.Encode(
            new DataRetentionMutationCheckpointV3(
                DataRetentionMutationCheckpointV3.CurrentVersion,
                Subtype: "reset-memory",
                Target: "5",
                new CovenantResetEffectArmV1(
                    OperationId,
                    CovenantRecoveryCheckpointCodec.EncodeEffectDigest(CovenantOperationGateFixture.Digest(7)),
                    CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
                    CovenantResetPhase.CanonicalApplied)));

        Assert.True(
            CovenantErasureCheckpointState.FromMutationCheckpoint(OperationId, payload, out _).IsFailure);

    }

    private static byte[] ResetPayload(CovenantResetPhase phase) =>
        CovenantRecoveryCheckpointCodec.Encode(
            new DataRetentionMutationCheckpointV3(
                DataRetentionMutationCheckpointV3.CurrentVersion,
                Subtype: "reset-memory",
                Target: "5",
                new CovenantResetEffectArmV1(
                    OperationId,
                    CovenantRecoveryCheckpointCodec.EncodeEffectDigest(CovenantOperationGateFixture.Digest(7)),
                    CovenantExclusiveOperation.CovenantReset,
                    phase)));

    private static byte[] FactoryPayload(CovenantResetPhase phase) =>
        CovenantRecoveryCheckpointCodec.Encode(
            new DataRetentionFactoryResetCheckpointV1(
                DataRetentionFactoryResetCheckpointV1.CurrentVersion,
                OperationId,
                CovenantRecoveryCheckpointCodec.EncodeEffectDigest(CovenantOperationGateFixture.Digest(7)),
                CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
                phase));

}
