using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

namespace RetroDownfall.Arcanum.Tests.GrimoireTransitions;

/// <summary>
/// The projection from a durable launch checkpoint to the journal's launch binding, and the digest
/// that ties the two together.
/// </summary>
/// <remarks>
/// The journal carries a digest of the launch rather than the launch itself, so the two durable
/// surfaces can be compared without either one copying the other's fields. That only works if the
/// digest is a function of every field: a launch whose source and target generations were transposed
/// must not produce the digest the journal already committed to.
/// </remarks>
public sealed class GrimoireOfflineTransitionLaunchBindingTests
{

    private static readonly Guid Operation = Guid.Parse("11111111-1111-4111-8111-111111111111");

    private static readonly Guid Source = Guid.Parse("22222222-2222-4222-8222-222222222222");

    private static readonly Guid Target = Guid.Parse("33333333-3333-4333-8333-333333333333");

    private const string Effect = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static CovenantOfflineTransitionLaunchV4 Reset() =>
        new(
            CovenantOfflineTransitionLaunchV4.CurrentVersion,
            Operation,
            LongRunningOperationKinds.DataRetentionMutation,
            nameof(LongRunningOperationRecoveryPolicy.ReconcileAndComplete),
            CovenantExclusiveOperation.CovenantReset,
            Effect,
            Source,
            Target,
            new CovenantOfflineTransitionEpochsV1(11, 22, 33),
            new CovenantOfflineTransitionEpochsV1(12, 23, 34),
            StartingRevision: 7);

    private static DataRetentionFactoryTransitionLaunchV2 Factory() =>
        new(
            DataRetentionFactoryTransitionLaunchV2.CurrentVersion,
            Operation,
            LongRunningOperationKinds.DataRetentionFactoryReset,
            nameof(LongRunningOperationRecoveryPolicy.RestartIdempotently),
            CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
            Effect,
            Source,
            Target,
            new CovenantOfflineTransitionEpochsV1(11, 22, 33),
            new CovenantOfflineTransitionEpochsV1(12, 23, 34),
            StartingRevision: 7);

    /// <summary>
    /// Every field is asserted separately and every value differs, because the failure this guards
    /// against is a projection that reads the right number of fields in the wrong order.
    /// </summary>
    [Fact]
    public void A_reset_launch_projects_every_field_onto_the_binding()
    {

        Result<GrimoireOfflineTransitionLaunchBinding> projected =
            GrimoireOfflineTransitionLaunch.FromLaunch(Reset());

        Assert.True(projected.IsSuccess);

        GrimoireOfflineTransitionLaunchBinding binding = projected.Value;

        Assert.Equal(Operation, binding.OperationId);

        Assert.Equal(LongRunningOperationKinds.DataRetentionMutation, binding.OperationKind);

        Assert.Equal(GrimoireOfflineTransitionKind.CovenantReset, binding.Kind);

        Assert.Equal(LongRunningOperationRecoveryPolicy.ReconcileAndComplete, binding.RecoveryPolicy);

        Assert.Equal(CovenantExclusiveOperation.CovenantReset, binding.Operation);

        Assert.Equal(Convert.FromHexString(Effect), binding.EffectDigest.Bytes);

        Assert.Equal(Source, binding.SourceDatasetGeneration);

        Assert.Equal(Target, binding.TargetDatasetGeneration);

        Assert.Equal(11UL, binding.SourceEpochs.AcceleratorEpoch);

        Assert.Equal(22UL, binding.SourceEpochs.KeyReclamationEpoch);

        Assert.Equal(33UL, binding.SourceEpochs.EnvelopeKeyEpoch);

        Assert.Equal(12UL, binding.TargetEpochs.AcceleratorEpoch);

        Assert.Equal(23UL, binding.TargetEpochs.KeyReclamationEpoch);

        Assert.Equal(34UL, binding.TargetEpochs.EnvelopeKeyEpoch);

        Assert.Equal(7L, binding.StartingRevision);

    }

    [Fact]
    public void A_factory_launch_projects_onto_its_own_transition_kind()
    {

        Result<GrimoireOfflineTransitionLaunchBinding> projected =
            GrimoireOfflineTransitionLaunch.FromLaunch(Factory());

        Assert.True(projected.IsSuccess);

        Assert.Equal(
            GrimoireOfflineTransitionKind.HealthyCatalogFactoryErasure,
            projected.Value.Kind);

        Assert.Equal(
            LongRunningOperationKinds.DataRetentionFactoryReset,
            projected.Value.OperationKind);

        Assert.Equal(
            LongRunningOperationRecoveryPolicy.RestartIdempotently,
            projected.Value.RecoveryPolicy);

    }

    /// <summary>
    /// The projection admits only what the codec admits, so there is one rule about what a launch is
    /// rather than a second one that could drift from it.
    /// </summary>
    [Fact]
    public void A_launch_the_codec_would_refuse_does_not_project()
    {

        Assert.True(
            GrimoireOfflineTransitionLaunch
                .FromLaunch(Reset() with { TargetDatasetGeneration = Source })
                .IsFailure);

        Assert.True(
            GrimoireOfflineTransitionLaunch
                .FromLaunch(Reset() with { Operation = CovenantExclusiveOperation.SchemaRepair })
                .IsFailure);

        Assert.True(
            GrimoireOfflineTransitionLaunch
                .FromLaunch(Factory() with { StartingRevision = -1 })
                .IsFailure);

    }

    /// <summary>
    /// A legacy checkpoint never becomes a launch, however well-formed it is.
    /// </summary>
    /// <remarks>
    /// The legacy shapes carry an owner and a phase and no target at all. A projection that filled
    /// the missing target in — from the live database, from a plan, from a default — would authorize
    /// an offline transition against a generation nobody ever committed to replacing, and the only
    /// evidence that it had done so would be gone by the time anyone looked. The refusal is asserted
    /// on payloads that are otherwise entirely valid, because a refusal that only fired on malformed
    /// input would prove nothing about this rule.
    /// </remarks>
    [Fact]
    public void A_valid_legacy_checkpoint_never_becomes_a_launch_binding()
    {

        DataRetentionMutationCheckpointV3 mutation = new(
            DataRetentionMutationCheckpointV3.CurrentVersion,
            Subtype: "reset-memory",
            Target: "5",
            new CovenantResetEffectArmV1(
                Operation,
                Effect,
                CovenantExclusiveOperation.CovenantReset,
                CovenantResetPhaseMachine.First));

        DataRetentionFactoryResetCheckpointV1 factory = new(
            DataRetentionFactoryResetCheckpointV1.CurrentVersion,
            Operation,
            Effect,
            CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
            CovenantResetPhaseMachine.First);

        Assert.True(
            CovenantRecoveryCheckpointCodec
                .DecodeDataRetentionMutation(CovenantRecoveryCheckpointCodec.Encode(mutation))
                .IsSuccess);

        Assert.True(
            CovenantRecoveryCheckpointCodec
                .DecodeDataRetentionFactoryReset(CovenantRecoveryCheckpointCodec.Encode(factory))
                .IsSuccess);

        Result<GrimoireOfflineTransitionLaunchBinding> fromMutation =
            GrimoireOfflineTransitionLaunch.FromLegacy(mutation);

        Result<GrimoireOfflineTransitionLaunchBinding> fromFactory =
            GrimoireOfflineTransitionLaunch.FromLegacy(factory);

        Assert.True(fromMutation.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, fromMutation.Error.Code);

        Assert.True(fromFactory.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, fromFactory.Error.Code);

    }

    [Fact]
    public void The_same_launch_digests_to_the_same_value()
    {

        Assert.Equal(
            GrimoireOfflineTransitionLaunch.FromLaunch(Reset()).Value.Digest.Bytes,
            GrimoireOfflineTransitionLaunch.FromLaunch(Reset()).Value.Digest.Bytes);

    }

    /// <summary>
    /// The two transitions never share a digest even when every other field matches, because the
    /// journal uses this value to decide which launch a row is.
    /// </summary>
    [Fact]
    public void The_two_transitions_digest_differently()
    {

        Assert.NotEqual(
            GrimoireOfflineTransitionLaunch.FromLaunch(Reset()).Value.Digest.Bytes,
            GrimoireOfflineTransitionLaunch.FromLaunch(Factory()).Value.Digest.Bytes);

    }

    /// <summary>
    /// One altered field, one different digest — for every field the binding carries.
    /// </summary>
    [Theory]
    [InlineData("operation-id")]
    [InlineData("operation-kind")]
    [InlineData("transition-kind")]
    [InlineData("recovery-policy")]
    [InlineData("exclusive-operation")]
    [InlineData("effect-digest")]
    [InlineData("source-generation")]
    [InlineData("target-generation")]
    [InlineData("source-accelerator-epoch")]
    [InlineData("source-key-reclamation-epoch")]
    [InlineData("source-envelope-key-epoch")]
    [InlineData("target-accelerator-epoch")]
    [InlineData("target-key-reclamation-epoch")]
    [InlineData("target-envelope-key-epoch")]
    [InlineData("starting-revision")]
    public void Every_launch_field_changes_the_digest(string field)
    {

        Assert.NotEqual(Binding().Digest.Bytes, Altered(field).Digest.Bytes);

    }

    private static GrimoireOfflineTransitionLaunchBinding Altered(string field) => field switch
    {

        "operation-id" =>
            Binding() with { OperationId = Guid.Parse("44444444-4444-4444-4444-444444444444") },

        "operation-kind" =>
            Binding() with { OperationKind = LongRunningOperationKinds.DataRetentionFactoryReset },

        "transition-kind" =>
            Binding() with { Kind = GrimoireOfflineTransitionKind.HealthyCatalogFactoryErasure },

        "recovery-policy" =>
            Binding() with { RecoveryPolicy = LongRunningOperationRecoveryPolicy.RestartIdempotently },

        "exclusive-operation" =>
            Binding() with { Operation = CovenantExclusiveOperation.HealthyCatalogFactoryErasure },

        "effect-digest" =>
            Binding() with { EffectDigest = new CovenantDigest([.. Enumerable.Repeat((byte)0x5a, 32)]) },

        "source-generation" =>
            Binding() with
            {
                SourceDatasetGeneration = Guid.Parse("55555555-5555-4555-8555-555555555555"),
            },

        "target-generation" =>
            Binding() with
            {
                TargetDatasetGeneration = Guid.Parse("66666666-6666-4666-8666-666666666666"),
            },

        "source-accelerator-epoch" =>
            Binding() with { SourceEpochs = new GrimoireOfflineTransitionEpochTuple(99, 22, 33) },

        "source-key-reclamation-epoch" =>
            Binding() with { SourceEpochs = new GrimoireOfflineTransitionEpochTuple(11, 99, 33) },

        "source-envelope-key-epoch" =>
            Binding() with { SourceEpochs = new GrimoireOfflineTransitionEpochTuple(11, 22, 99) },

        "target-accelerator-epoch" =>
            Binding() with { TargetEpochs = new GrimoireOfflineTransitionEpochTuple(99, 23, 34) },

        "target-key-reclamation-epoch" =>
            Binding() with { TargetEpochs = new GrimoireOfflineTransitionEpochTuple(12, 99, 34) },

        "target-envelope-key-epoch" =>
            Binding() with { TargetEpochs = new GrimoireOfflineTransitionEpochTuple(12, 23, 99) },

        _ => Binding() with { StartingRevision = 8 },

    };

    /// <summary>
    /// The journal binding takes its launch digest and expected revision from the launch itself, so
    /// no caller can publish a journal that claims to be bound to a launch it is not.
    /// </summary>
    [Fact]
    public void The_journal_binding_carries_the_launch_digest_and_the_expected_revision()
    {

        GrimoireOfflineTransitionLaunchBinding launch =
            GrimoireOfflineTransitionLaunch.FromLaunch(Reset()).Value;

        Result<GrimoireOfflineTransitionBinding> journal = GrimoireOfflineTransitionLaunch.JournalBinding(
            launch,
            slotEpoch: 3,
            payloadVersion: 1,
            expectedDatabaseOperationRevision: 8,
            parentReceiptBindingDigest: null);

        Assert.True(journal.IsSuccess);

        Assert.Equal(launch.Digest, journal.Value.DatabaseOperationLaunchBindingDigest);

        Assert.Equal(8UL, journal.Value.ExpectedDatabaseOperationRevision);

        Assert.Equal(launch.OperationId, journal.Value.OperationId);

        Assert.Equal(launch.Kind, journal.Value.Kind);

        Assert.Equal(launch.EffectDigest, journal.Value.EffectDigest);

        Assert.Equal(launch.SourceDatasetGeneration, journal.Value.SourceDatasetGeneration);

        Assert.Equal(launch.TargetDatasetGeneration, journal.Value.TargetDatasetGeneration);

        Assert.Equal(launch.SourceEpochs, journal.Value.SourceEpochs);

        Assert.Equal(launch.TargetEpochs, journal.Value.TargetEpochs);

        Assert.Equal(3UL, journal.Value.SlotEpoch);

        Assert.Equal((byte)1, journal.Value.PayloadVersion);

        Assert.Null(journal.Value.ParentReceiptBindingDigest);

    }

    /// <summary>
    /// Committing the launch checkpoint advances the row's revision, so the revision a journal
    /// expects the terminal compare-exchange to find is always past the one the launch recorded.
    /// </summary>
    /// <remarks>
    /// An expected revision at or below the launch's own would name a row state from before the
    /// checkpoint existed. The compare-exchange would then either miss entirely or, worse, match a
    /// row that had been reset for another attempt.
    /// </remarks>
    [Theory]
    [InlineData(7L)]
    [InlineData(6L)]
    [InlineData(0L)]
    public void A_journal_binding_may_not_expect_a_revision_the_launch_already_passed(long expected)
    {

        Assert.True(
            GrimoireOfflineTransitionLaunch.JournalBinding(
                GrimoireOfflineTransitionLaunch.FromLaunch(Reset()).Value,
                slotEpoch: 3,
                payloadVersion: 1,
                expectedDatabaseOperationRevision: expected,
                parentReceiptBindingDigest: null).IsFailure);

    }

    [Fact]
    public void A_journal_binding_requires_a_slot_epoch_and_a_payload_version()
    {

        GrimoireOfflineTransitionLaunchBinding launch =
            GrimoireOfflineTransitionLaunch.FromLaunch(Reset()).Value;

        Assert.True(
            GrimoireOfflineTransitionLaunch.JournalBinding(
                launch,
                slotEpoch: 0,
                payloadVersion: 1,
                expectedDatabaseOperationRevision: 8,
                parentReceiptBindingDigest: null).IsFailure);

        Assert.True(
            GrimoireOfflineTransitionLaunch.JournalBinding(
                launch,
                slotEpoch: 3,
                payloadVersion: 0,
                expectedDatabaseOperationRevision: 8,
                parentReceiptBindingDigest: null).IsFailure);

    }

    [Fact]
    public void A_journal_binding_refuses_an_invalid_parent_receipt_digest()
    {

        Assert.True(
            GrimoireOfflineTransitionLaunch.JournalBinding(
                GrimoireOfflineTransitionLaunch.FromLaunch(Factory()).Value,
                slotEpoch: 3,
                payloadVersion: 1,
                expectedDatabaseOperationRevision: 8,
                parentReceiptBindingDigest: default(CovenantDigest)).IsFailure);

    }

    private static GrimoireOfflineTransitionLaunchBinding Binding() =>
        GrimoireOfflineTransitionLaunch.FromLaunch(Reset()).Value;

}
