using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The V4 Covenant and V2 factory offline-transition launch checkpoints.
/// </summary>
/// <remarks>
/// A launch checkpoint is the immutable half of an offline transition's database row: it names the
/// operation, the transition, the recovery policy, the canonical effect, the exact source state the
/// transition was planned against, and the exact target state it preselected. Every one of those
/// fields is authority a later process would otherwise have to infer, and a payload that decoded
/// with one of them altered would authorize a destructive plan nobody committed.
///
/// <para>The per-field refusals below all carry the same error code on purpose, so each test uses a
/// value distinct from every other field's. A checkpoint whose source and target generations were
/// transposed would decode and be wrong, and one shared sentinel across every field would hide
/// exactly that.</para>
/// </remarks>
public sealed class CovenantOfflineTransitionLaunchCodecTests
{

    private static readonly Guid Operation = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly Guid Source = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly Guid Target = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private const string Effect = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static CovenantOfflineTransitionEpochsV1 SourceEpochs => new(11, 22, 33);

    private static CovenantOfflineTransitionEpochsV1 TargetEpochs => new(12, 23, 34);

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
            SourceEpochs,
            TargetEpochs,
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
            SourceEpochs,
            TargetEpochs,
            StartingRevision: 7);

    [Fact]
    public void The_two_launch_versions_are_the_ones_this_build_writes()
    {

        Assert.Equal(4, CovenantOfflineTransitionLaunchV4.CurrentVersion);

        Assert.Equal(2, DataRetentionFactoryTransitionLaunchV2.CurrentVersion);

    }

    [Fact]
    public void A_reset_launch_round_trips_every_field_it_carries()
    {

        Result<CovenantOfflineTransitionLaunchV4> decoded =
            CovenantRecoveryCheckpointCodec.DecodeCovenantOfflineTransitionLaunch(
                CovenantRecoveryCheckpointCodec.Encode(Reset()));

        Assert.True(decoded.IsSuccess);

        Assert.Equal(Reset(), decoded.Value);

    }

    [Fact]
    public void A_factory_launch_round_trips_every_field_it_carries()
    {

        Result<DataRetentionFactoryTransitionLaunchV2> decoded =
            CovenantRecoveryCheckpointCodec.DecodeDataRetentionFactoryTransitionLaunch(
                CovenantRecoveryCheckpointCodec.Encode(Factory()));

        Assert.True(decoded.IsSuccess);

        Assert.Equal(Factory(), decoded.Value);

    }

    /// <summary>
    /// The exclusive operation and the phase travel as names rather than numbers, because a
    /// reordered member would otherwise repoint an already-committed launch at a different plan.
    /// </summary>
    [Fact]
    public void A_launch_encodes_its_operation_code_by_name()
    {

        string json = Encoding.UTF8.GetString(CovenantRecoveryCheckpointCodec.Encode(Reset()));

        Assert.Contains("\"CovenantReset\"", json, StringComparison.Ordinal);

        Assert.Contains("\"ReconcileAndComplete\"", json, StringComparison.Ordinal);

    }

    [Fact]
    public void A_launch_carrying_an_unknown_member_is_refused()
    {

        string json = Encoding.UTF8.GetString(CovenantRecoveryCheckpointCodec.Encode(Reset()));

        AssertUnrecoverableReset(
            Encoding.UTF8.GetBytes(json.Insert(1, "\"unmappedInvariant\":true,")));

    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(0)]
    public void A_reset_launch_under_any_other_version_discriminator_is_refused(int version)
    {

        AssertUnrecoverableReset(
            CovenantRecoveryCheckpointCodec.Encode(Reset() with { Version = version }));

    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(0)]
    public void A_factory_launch_under_any_other_version_discriminator_is_refused(int version)
    {

        AssertUnrecoverableFactory(
            CovenantRecoveryCheckpointCodec.Encode(Factory() with { Version = version }));

    }

    [Fact]
    public void An_empty_operation_identity_is_refused()
    {

        AssertUnrecoverableReset(
            CovenantRecoveryCheckpointCodec.Encode(Reset() with { OperationId = Guid.Empty }));

        AssertUnrecoverableFactory(
            CovenantRecoveryCheckpointCodec.Encode(Factory() with { OperationId = Guid.Empty }));

    }

    /// <summary>
    /// The launch names the durable ledger kind its row is filed under, and only that kind. A launch
    /// whose kind disagreed with its row would reconcile an operation it does not describe.
    /// </summary>
    [Theory]
    [InlineData(LongRunningOperationKinds.DataRetentionFactoryReset)]
    [InlineData(LongRunningOperationKinds.DataRetentionPrune)]
    [InlineData("")]
    public void A_reset_launch_naming_another_operation_kind_is_refused(string kind)
    {

        AssertUnrecoverableReset(
            CovenantRecoveryCheckpointCodec.Encode(Reset() with { OperationKind = kind }));

    }

    [Theory]
    [InlineData(LongRunningOperationKinds.DataRetentionMutation)]
    [InlineData(LongRunningOperationKinds.DataRetentionPrune)]
    [InlineData("")]
    public void A_factory_launch_naming_another_operation_kind_is_refused(string kind)
    {

        AssertUnrecoverableFactory(
            CovenantRecoveryCheckpointCodec.Encode(Factory() with { OperationKind = kind }));

    }

    /// <summary>
    /// The recovery policy is the one the ledger registers for that kind, and nothing else: a launch
    /// that claimed a different policy would ask recovery to restart an operation the registry says
    /// must be reconciled.
    /// </summary>
    [Theory]
    [InlineData(nameof(LongRunningOperationRecoveryPolicy.RestartIdempotently))]
    [InlineData(nameof(LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint))]
    [InlineData("2")]
    [InlineData("")]
    public void A_reset_launch_claiming_another_recovery_policy_is_refused(string policy)
    {

        AssertUnrecoverableReset(
            CovenantRecoveryCheckpointCodec.Encode(Reset() with { RecoveryPolicy = policy }));

    }

    [Theory]
    [InlineData(nameof(LongRunningOperationRecoveryPolicy.ReconcileAndComplete))]
    [InlineData(nameof(LongRunningOperationRecoveryPolicy.AbandonSafely))]
    [InlineData("1")]
    [InlineData("")]
    public void A_factory_launch_claiming_another_recovery_policy_is_refused(string policy)
    {

        AssertUnrecoverableFactory(
            CovenantRecoveryCheckpointCodec.Encode(Factory() with { RecoveryPolicy = policy }));

    }

    [Fact]
    public void A_launch_carrying_the_other_transition_s_exclusive_operation_is_refused()
    {

        AssertUnrecoverableReset(
            CovenantRecoveryCheckpointCodec.Encode(
                Reset() with { Operation = CovenantExclusiveOperation.HealthyCatalogFactoryErasure }));

        AssertUnrecoverableFactory(
            CovenantRecoveryCheckpointCodec.Encode(
                Factory() with { Operation = CovenantExclusiveOperation.CovenantReset }));

    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("NOTHEXNOTHEXNOTHEXNOTHEXNOTHEXNOTHEXNOTHEXNOTHEXNOTHEXNOTHEXNOTH")]
    public void An_effect_digest_that_is_not_thirty_two_canonical_bytes_is_refused(string digest)
    {

        AssertUnrecoverableReset(
            CovenantRecoveryCheckpointCodec.Encode(Reset() with { EffectDigest = digest }));

    }

    [Fact]
    public void An_uppercase_effect_digest_is_refused_so_one_effect_has_one_encoding()
    {

        AssertUnrecoverableReset(
            CovenantRecoveryCheckpointCodec.Encode(
                Reset() with { EffectDigest = new string('A', 64) }));

    }

    [Fact]
    public void An_absent_source_or_target_generation_is_refused()
    {

        AssertUnrecoverableReset(
            CovenantRecoveryCheckpointCodec.Encode(
                Reset() with { SourceDatasetGeneration = Guid.Empty }));

        AssertUnrecoverableReset(
            CovenantRecoveryCheckpointCodec.Encode(
                Reset() with { TargetDatasetGeneration = Guid.Empty }));

    }

    /// <summary>
    /// A transition that preselected the generation it is replacing has preselected nothing: the
    /// canonical transaction would then be unable to tell a committed replacement from an untouched
    /// database.
    /// </summary>
    [Fact]
    public void A_target_generation_equal_to_the_source_is_refused()
    {

        AssertUnrecoverableReset(
            CovenantRecoveryCheckpointCodec.Encode(
                Reset() with { TargetDatasetGeneration = Source }));

        AssertUnrecoverableFactory(
            CovenantRecoveryCheckpointCodec.Encode(
                Factory() with { TargetDatasetGeneration = Source }));

    }

    /// <summary>
    /// The canonical family transaction advances each of the three epochs by exactly one, so the
    /// preselected target is computable before the transaction runs and is refused when it is not
    /// that value. Each member is altered on its own, because a launch whose accelerator and
    /// envelope targets were transposed would still satisfy a rule that only checked the set.
    /// </summary>
    [Theory]
    [InlineData(13, 23, 34)]
    [InlineData(12, 24, 34)]
    [InlineData(12, 23, 35)]
    [InlineData(11, 23, 34)]
    [InlineData(12, 22, 34)]
    [InlineData(12, 23, 33)]
    public void A_target_epoch_that_is_not_its_source_successor_is_refused(
        ulong accelerator,
        ulong reclamation,
        ulong envelope)
    {

        AssertUnrecoverableReset(
            CovenantRecoveryCheckpointCodec.Encode(
                Reset() with
                {
                    TargetEpochs = new CovenantOfflineTransitionEpochsV1(
                        accelerator,
                        reclamation,
                        envelope),
                }));

    }

    /// <summary>
    /// The transposition the successor rule alone cannot see: every target is one greater than
    /// <em>some</em> source, but not than its own.
    /// </summary>
    [Fact]
    public void A_launch_whose_epoch_members_were_transposed_is_refused()
    {

        AssertUnrecoverableReset(
            CovenantRecoveryCheckpointCodec.Encode(
                Reset() with
                {
                    TargetEpochs = new CovenantOfflineTransitionEpochsV1(23, 34, 12),
                }));

    }

    /// <summary>
    /// The persisted epochs are positive by schema check, and a saturated one cannot advance at all.
    /// A launch that preselected a target beyond the ceiling would be refused by the database after
    /// the transition had already closed admission.
    /// </summary>
    [Theory]
    [InlineData(0UL)]
    [InlineData((ulong)long.MaxValue)]
    public void A_source_epoch_outside_the_advanceable_range_is_refused(ulong accelerator)
    {

        AssertUnrecoverableReset(
            CovenantRecoveryCheckpointCodec.Encode(
                Reset() with
                {
                    SourceEpochs = new CovenantOfflineTransitionEpochsV1(accelerator, 22, 33),
                    TargetEpochs = new CovenantOfflineTransitionEpochsV1(accelerator + 1, 23, 34),
                }));

    }

    [Fact]
    public void A_negative_starting_revision_is_refused()
    {

        AssertUnrecoverableReset(
            CovenantRecoveryCheckpointCodec.Encode(Reset() with { StartingRevision = -1 }));

        AssertUnrecoverableFactory(
            CovenantRecoveryCheckpointCodec.Encode(Factory() with { StartingRevision = -1 }));

    }

    /// <summary>
    /// The bytes a retired shape left behind are refused by the launch decoders, on their own terms.
    /// </summary>
    /// <remarks>
    /// The payloads are written out rather than built from a record, because the records are gone -
    /// what is not gone is the rows an interrupted installation is still carrying. They are also
    /// deliberately well-formed for what they claim to be: a refusal that only fired on malformed
    /// input would be a rule about parsing rather than about which shapes carry launch authority.
    /// </remarks>
    [Fact]
    public void A_retired_checkpoint_payload_is_never_read_as_a_launch()
    {

        AssertUnrecoverableReset(RetiredCovenantCheckpoints.Mutation(Operation, Effect));

        AssertUnrecoverableFactory(RetiredCovenantCheckpoints.FactoryReset(Operation, Effect));

        AssertUnrecoverableFactory(RetiredCovenantCheckpoints.Mutation(Operation, Effect));

        AssertUnrecoverableReset(RetiredCovenantCheckpoints.FactoryReset(Operation, Effect));

    }

    /// <summary>
    /// The two launch shapes differ only in the triple they pin, so each has to refuse the other's
    /// bytes on that triple rather than on the shape.
    /// </summary>
    [Fact]
    public void Each_launch_decoder_refuses_the_other_transition_s_launch()
    {

        AssertUnrecoverableReset(CovenantRecoveryCheckpointCodec.Encode(Factory()));

        AssertUnrecoverableFactory(CovenantRecoveryCheckpointCodec.Encode(Reset()));

    }

    /// <summary>
    /// A payload carrying the reset version but the factory transition's kind, policy, and operation
    /// is refused by the version check's own decoder — the pin is the whole triple, not the number.
    /// </summary>
    [Fact]
    public void A_launch_at_the_right_version_but_the_wrong_transition_is_refused()
    {

        AssertUnrecoverableReset(
            CovenantRecoveryCheckpointCodec.Encode(
                Reset() with
                {
                    OperationKind = LongRunningOperationKinds.DataRetentionFactoryReset,
                    RecoveryPolicy = nameof(LongRunningOperationRecoveryPolicy.RestartIdempotently),
                    Operation = CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
                }));

    }

    /// <summary>
    /// The oversized payload is deliberately well-formed and otherwise valid — the same bytes with
    /// the padding removed decode — so only the length guard can refuse it.
    /// </summary>
    /// <remarks>
    /// Every field of a launch is either fixed-width or pinned to a declared constant, so there is
    /// no field to pad. Insignificant whitespace is the one way to build a payload that is oversized
    /// and would otherwise decode, which is exactly what the bound has to stop before it parses.
    /// </remarks>
    [Fact]
    public void An_oversized_but_otherwise_valid_launch_is_refused_before_it_is_parsed()
    {

        string json = Encoding.UTF8.GetString(CovenantRecoveryCheckpointCodec.Encode(Reset()));

        byte[] oversized = Encoding.UTF8.GetBytes(
            json.Insert(1, new string(' ', CovenantRecoveryJsonContext.MaxCheckpointBytes)));

        Assert.True(oversized.Length > CovenantRecoveryJsonContext.MaxCheckpointBytes);

        AssertUnrecoverableReset(oversized);

        byte[] admissible = Encoding.UTF8.GetBytes(json.Insert(1, new string(' ', 64)));

        Assert.True(admissible.Length <= CovenantRecoveryJsonContext.MaxCheckpointBytes);

        Assert.True(
            CovenantRecoveryCheckpointCodec
                .DecodeCovenantOfflineTransitionLaunch(admissible)
                .IsSuccess);

    }

    [Fact]
    public void An_empty_payload_is_refused()
    {

        AssertUnrecoverableReset([]);

        AssertUnrecoverableFactory([]);

    }

    [Fact]
    public void Malformed_bytes_fail_as_a_typed_result_rather_than_an_escaping_exception()
    {

        AssertUnrecoverableReset("not json"u8.ToArray());

        AssertUnrecoverableFactory("not json"u8.ToArray());

    }

    [Fact]
    public void The_largest_legitimate_launch_fits_well_inside_the_cap()
    {

        int reset = CovenantRecoveryCheckpointCodec.Encode(Reset()).Length;

        int factory = CovenantRecoveryCheckpointCodec.Encode(Factory()).Length;

        Assert.True(reset * 2 < CovenantRecoveryJsonContext.MaxCheckpointBytes);

        Assert.True(factory * 2 < CovenantRecoveryJsonContext.MaxCheckpointBytes);

    }

    private static void AssertUnrecoverableReset(byte[] payload)
    {

        Result<CovenantOfflineTransitionLaunchV4> decoded =
            CovenantRecoveryCheckpointCodec.DecodeCovenantOfflineTransitionLaunch(payload);

        Assert.True(decoded.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, decoded.Error.Code);

    }

    private static void AssertUnrecoverableFactory(byte[] payload)
    {

        Result<DataRetentionFactoryTransitionLaunchV2> decoded =
            CovenantRecoveryCheckpointCodec.DecodeDataRetentionFactoryTransitionLaunch(payload);

        Assert.True(decoded.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, decoded.Error.Code);

    }

}
