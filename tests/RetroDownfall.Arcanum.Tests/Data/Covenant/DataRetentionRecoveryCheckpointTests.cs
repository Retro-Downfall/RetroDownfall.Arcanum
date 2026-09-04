using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The V4 Covenant offline-transition launch and the V2 factory transition launch, as the codec
/// writes them and reads them back.
/// </summary>
/// <remarks>
/// A launch is the only durable record an interrupted Covenant erasure leaves in its ledger row, and
/// three of its fields are the identity: the immutable server operation identity, the canonical
/// 32-byte effect digest, and the exact operation code. Recovery reconstructs its exclusive owner
/// from those three and nothing else, so a payload that decoded with one of them altered would adopt
/// a closed scope belonging to a different operation. Everything else the launch carries — the ledger
/// kind, the recovery policy, the source and target generations, the preselected epochs — is the
/// plan, and no part of the plan may reach the owner.
///
/// <para>The phase these shapes used to carry is gone. A launch records what was committed to, and an
/// offline transition's progress past that point is the authenticated journal's to state, so the
/// durable enum this file has to keep honest is now the operation code rather than the phase.</para>
///
/// <para>Every failure is one code, because an operator told "wrong operation code" and one told
/// "unknown recovery policy" does the same thing: look at the operation, not at the bytes.</para>
/// </remarks>
public sealed class DataRetentionRecoveryCheckpointTests
{

    private static readonly Guid Operation = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private static readonly Guid SourceGeneration = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private static readonly Guid TargetGeneration = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private static readonly string Effect = new('a', 64);

    private static readonly CovenantOfflineTransitionEpochsV1 SourceEpochs = new(11, 22, 33);

    /// <summary>The widest epoch tuple a launch may preselect a successor for.</summary>
    private static readonly CovenantOfflineTransitionEpochsV1 WidestEpochs =
        new((ulong)long.MaxValue - 3, (ulong)long.MaxValue - 2, (ulong)long.MaxValue - 1);

    private static CovenantOfflineTransitionEpochsV1 Successor(
        CovenantOfflineTransitionEpochsV1 source) =>
        new(
            source.AcceleratorEpoch + 1,
            source.KeyReclamationEpoch + 1,
            source.EnvelopeKeyEpoch + 1);

    private static CovenantOfflineTransitionLaunchV4 Reset(
        CovenantOfflineTransitionEpochsV1? source = null)
    {

        CovenantOfflineTransitionEpochsV1 epochs = source ?? SourceEpochs;

        return new CovenantOfflineTransitionLaunchV4(
            CovenantOfflineTransitionLaunchV4.CurrentVersion,
            Operation,
            LongRunningOperationKinds.DataRetentionMutation,
            nameof(LongRunningOperationRecoveryPolicy.ReconcileAndComplete),
            CovenantExclusiveOperation.CovenantReset,
            Effect,
            SourceGeneration,
            TargetGeneration,
            epochs,
            Successor(epochs),
            StartingRevision: 7);

    }

    private static DataRetentionFactoryTransitionLaunchV2 FactoryReset(
        CovenantOfflineTransitionEpochsV1? source = null)
    {

        CovenantOfflineTransitionEpochsV1 epochs = source ?? SourceEpochs;

        return new DataRetentionFactoryTransitionLaunchV2(
            DataRetentionFactoryTransitionLaunchV2.CurrentVersion,
            Operation,
            LongRunningOperationKinds.DataRetentionFactoryReset,
            nameof(LongRunningOperationRecoveryPolicy.RestartIdempotently),
            CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
            Effect,
            SourceGeneration,
            TargetGeneration,
            epochs,
            Successor(epochs),
            StartingRevision: 7);

    }

    /// <summary>
    /// The version each shape writes is the version the recovery matrix admits for its kind.
    /// </summary>
    /// <remarks>
    /// Asserted against the registry rather than only against a literal, because the two are the same
    /// promise made in two places. A build that raised one without the other would either write rows
    /// its own recovery pass refuses, or admit a payload no code in it can read.
    /// </remarks>
    [Fact]
    public void The_two_checkpoint_versions_are_the_ones_the_registry_pins()
    {

        Assert.Equal(4, CovenantOfflineTransitionLaunchV4.CurrentVersion);

        Assert.Equal(2, DataRetentionFactoryTransitionLaunchV2.CurrentVersion);

        Assert.Equal(
            CovenantOfflineTransitionLaunchV4.CurrentVersion,
            LongRunningOperationRecoveryRegistry
                .Descriptors[LongRunningOperationKinds.DataRetentionMutation]
                .MaxCheckpointVersion);

        Assert.Equal(
            DataRetentionFactoryTransitionLaunchV2.CurrentVersion,
            LongRunningOperationRecoveryRegistry
                .Descriptors[LongRunningOperationKinds.DataRetentionFactoryReset]
                .MaxCheckpointVersion);

    }

    /// <summary>
    /// The epoch tuple is the one part of a launch whose value varies across the whole admissible
    /// range, so the round trip is asserted across that range rather than at one convenient point.
    /// </summary>
    /// <remarks>
    /// The tuple at 2^53 and above is the case a round trip through a floating-point number would
    /// silently corrupt: the decoded epoch would still be a plausible epoch, one or two off, and the
    /// transition would then verify a replaced family against a counter value it never committed to.
    /// The top row is the highest tuple a launch may preselect a successor for at all.
    /// </remarks>
    [Theory]
    [InlineData(1UL, 2UL, 3UL)]
    [InlineData(11UL, 22UL, 33UL)]
    [InlineData(9_007_199_254_740_993UL, 9_007_199_254_740_994UL, 9_007_199_254_740_995UL)]
    [InlineData((ulong)long.MaxValue - 3, (ulong)long.MaxValue - 2, (ulong)long.MaxValue - 1)]
    public void A_v4_covenant_launch_round_trips_from_every_admissible_epoch_tuple(
        ulong accelerator,
        ulong reclamation,
        ulong envelope)
    {

        CovenantOfflineTransitionEpochsV1 epochs =
            new(accelerator, reclamation, envelope);

        Result<CovenantOfflineTransitionLaunchV4> decoded =
            CovenantRecoveryCheckpointCodec.DecodeCovenantOfflineTransitionLaunch(
                CovenantRecoveryCheckpointCodec.Encode(Reset(epochs)));

        Assert.True(decoded.IsSuccess);

        Assert.Equal(Reset(epochs), decoded.Value);

    }

    [Theory]
    [InlineData(1UL, 2UL, 3UL)]
    [InlineData(11UL, 22UL, 33UL)]
    [InlineData(9_007_199_254_740_993UL, 9_007_199_254_740_994UL, 9_007_199_254_740_995UL)]
    [InlineData((ulong)long.MaxValue - 3, (ulong)long.MaxValue - 2, (ulong)long.MaxValue - 1)]
    public void A_v2_factory_launch_round_trips_from_every_admissible_epoch_tuple(
        ulong accelerator,
        ulong reclamation,
        ulong envelope)
    {

        CovenantOfflineTransitionEpochsV1 epochs =
            new(accelerator, reclamation, envelope);

        Result<DataRetentionFactoryTransitionLaunchV2> decoded =
            CovenantRecoveryCheckpointCodec.DecodeDataRetentionFactoryTransitionLaunch(
                CovenantRecoveryCheckpointCodec.Encode(FactoryReset(epochs)));

        Assert.True(decoded.IsSuccess);

        Assert.Equal(FactoryReset(epochs), decoded.Value);

    }

    /// <summary>
    /// The recovery policy travels as its declared name, so a renumbered enum cannot silently change
    /// how an already-committed transition is recovered.
    /// </summary>
    /// <remarks>
    /// The numeric code is also a public operator-API value governed by a different compatibility
    /// promise. A durable payload that borrowed it would let a wire renumbering decide whether an
    /// interrupted destructive transition is reconciled or restarted, and that is the one decision no
    /// wire change may make.
    /// </remarks>
    [Fact]
    public void Recovery_policies_travel_as_names_so_a_renumbered_enum_cannot_silently_change_a_recovery_class()
    {

        string json = Encoding.UTF8.GetString(CovenantRecoveryCheckpointCodec.Encode(Reset()));

        Assert.Contains(
            "\"recoveryPolicy\":\"ReconcileAndComplete\"",
            json,
            StringComparison.Ordinal);

        Assert.DoesNotContain("\"recoveryPolicy\":2", json, StringComparison.Ordinal);

    }

    [Fact]
    public void The_operation_code_travels_as_a_name_too()
    {

        string json = Encoding.UTF8.GetString(CovenantRecoveryCheckpointCodec.Encode(Reset()));

        Assert.Contains("\"operation\":\"CovenantReset\"", json, StringComparison.Ordinal);

        Assert.DoesNotContain("\"operation\":7", json, StringComparison.Ordinal);

    }

    [Fact]
    public void A_numeric_operation_code_is_refused()
    {

        byte[] payload = Encoding.UTF8.GetBytes(
            Encoding.UTF8
                .GetString(CovenantRecoveryCheckpointCodec.Encode(Reset()))
                .Replace("\"operation\":\"CovenantReset\"", "\"operation\":7", StringComparison.Ordinal));

        AssertUnrecoverableCovenantLaunch(payload);

    }

    [Fact]
    public void An_unknown_operation_code_name_is_refused()
    {

        byte[] payload = Encoding.UTF8.GetBytes(
            Encoding.UTF8
                .GetString(CovenantRecoveryCheckpointCodec.Encode(Reset()))
                .Replace(
                    "\"operation\":\"CovenantReset\"",
                    "\"operation\":\"CovenantErased\"",
                    StringComparison.Ordinal));

        AssertUnrecoverableCovenantLaunch(payload);

    }

    /// <summary>
    /// A recovery policy spelled as its numeric code is refused even though the enum would parse it.
    /// </summary>
    /// <remarks>
    /// <c>Enum.TryParse</c> accepts <c>"2"</c> as readily as it accepts the member name, so a payload
    /// spelling the policy numerically would be admitted under a name it never wrote — which is
    /// exactly the coupling to the wire code the field exists to avoid.
    /// </remarks>
    [Fact]
    public void A_recovery_policy_spelled_as_its_numeric_code_is_refused()
    {

        byte[] payload = Encoding.UTF8.GetBytes(
            Encoding.UTF8
                .GetString(CovenantRecoveryCheckpointCodec.Encode(Reset()))
                .Replace(
                    "\"recoveryPolicy\":\"ReconcileAndComplete\"",
                    "\"recoveryPolicy\":\"2\"",
                    StringComparison.Ordinal));

        AssertUnrecoverableCovenantLaunch(payload);

    }

    [Fact]
    public void An_unknown_field_fails_recovery_rather_than_being_dropped()
    {

        string json = Encoding.UTF8.GetString(CovenantRecoveryCheckpointCodec.Encode(Reset()));

        AssertUnrecoverableCovenantLaunch(
            Encoding.UTF8.GetBytes(json.Insert(1, "\"unmappedInvariant\":true,")));

    }

    [Fact]
    public void A_future_version_discriminator_fails_recovery()
    {

        AssertUnrecoverableCovenantLaunch(
            CovenantRecoveryCheckpointCodec.Encode(Reset() with { Version = 5 }));

        Result<DataRetentionFactoryTransitionLaunchV2> factory =
            CovenantRecoveryCheckpointCodec.DecodeDataRetentionFactoryTransitionLaunch(
                CovenantRecoveryCheckpointCodec.Encode(FactoryReset() with { Version = 3 }));

        Assert.True(factory.IsFailure);

    }

    /// <summary>
    /// A Covenant launch may name only <see cref="CovenantExclusiveOperation.CovenantReset"/>, and a
    /// factory launch only <see cref="CovenantExclusiveOperation.HealthyCatalogFactoryErasure"/>.
    /// Anything else would mint an exclusive owner for an operation that never closed admission.
    /// </summary>
    [Fact]
    public void A_foreign_operation_code_is_refused_on_both_launches()
    {

        AssertUnrecoverableCovenantLaunch(
            CovenantRecoveryCheckpointCodec.Encode(
                Reset() with
                {
                    Operation = CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
                }));

        Result<DataRetentionFactoryTransitionLaunchV2> factory =
            CovenantRecoveryCheckpointCodec.DecodeDataRetentionFactoryTransitionLaunch(
                CovenantRecoveryCheckpointCodec.Encode(
                    FactoryReset() with { Operation = CovenantExclusiveOperation.CovenantReset }));

        Assert.True(factory.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, factory.Error.Code);

    }

    [Fact]
    public void An_empty_operation_identity_is_refused()
    {

        AssertUnrecoverableCovenantLaunch(
            CovenantRecoveryCheckpointCodec.Encode(Reset() with { OperationId = Guid.Empty }));

    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("NOTHEXNOTHEXNOTHEXNOTHEXNOTHEXNOTHEXNOTHEXNOTHEXNOTHEXNOTHEXNOTH")]
    public void An_effect_digest_that_is_not_thirty_two_canonical_bytes_is_refused(string digest)
    {

        AssertUnrecoverableCovenantLaunch(
            CovenantRecoveryCheckpointCodec.Encode(Reset() with { EffectDigest = digest }));

    }

    [Fact]
    public void An_uppercase_effect_digest_is_refused_so_one_effect_has_one_encoding()
    {

        AssertUnrecoverableCovenantLaunch(
            CovenantRecoveryCheckpointCodec.Encode(
                Reset() with { EffectDigest = new string('A', 64) }));

    }

    /// <summary>
    /// The oversized payload is deliberately well-formed and otherwise valid, so only the length
    /// guard can refuse it.
    /// </summary>
    /// <remarks>
    /// A blob of NUL bytes would fail JSON parsing whether or not the bound existed, which proves
    /// nothing about the bound. Recovery runs before readiness, so the one thing that must be true
    /// is that a hostile or corrupt payload cannot make it allocate in proportion to itself.
    ///
    /// <para>Every field of a launch is either fixed-width or pinned to a declared constant, so there
    /// is no field left to pad. Insignificant whitespace is the only way to build a payload that is
    /// oversized and would otherwise decode exactly as the unpadded bytes do.</para>
    /// </remarks>
    [Fact]
    public void An_oversized_but_otherwise_valid_payload_is_refused_before_it_is_parsed()
    {

        byte[] encoded = CovenantRecoveryCheckpointCodec.Encode(Reset());

        string json = Encoding.UTF8.GetString(encoded);

        byte[] oversized = Encoding.UTF8.GetBytes(
            json.Insert(
                1,
                new string(' ', CovenantRecoveryJsonContext.MaxCheckpointBytes + 1 - encoded.Length)));

        Assert.True(oversized.Length > CovenantRecoveryJsonContext.MaxCheckpointBytes);

        AssertUnrecoverableCovenantLaunch(oversized);

        // The same payload one byte under the cap decodes, so the refusal above is the bound and
        // not some other validation rejecting the padding.
        byte[] admissible = Encoding.UTF8.GetBytes(
            json.Insert(
                1,
                new string(' ', CovenantRecoveryJsonContext.MaxCheckpointBytes - encoded.Length)));

        Assert.True(admissible.Length <= CovenantRecoveryJsonContext.MaxCheckpointBytes);

        Assert.True(
            CovenantRecoveryCheckpointCodec
                .DecodeCovenantOfflineTransitionLaunch(admissible)
                .IsSuccess);

    }

    [Fact]
    public void An_empty_payload_is_refused()
    {

        AssertUnrecoverableCovenantLaunch([]);

    }

    [Fact]
    public void Malformed_bytes_fail_as_a_typed_result_rather_than_an_escaping_exception()
    {

        AssertUnrecoverableCovenantLaunch("not json"u8.ToArray());

        Result<DataRetentionFactoryTransitionLaunchV2> factory =
            CovenantRecoveryCheckpointCodec.DecodeDataRetentionFactoryTransitionLaunch(
                "not json"u8.ToArray());

        Assert.True(factory.IsFailure);

    }

    /// <summary>
    /// The widest launch either shape can legitimately write — the highest preselectable epoch tuple
    /// and the highest possible starting revision — still leaves the cap most of its headroom.
    /// </summary>
    [Fact]
    public void The_largest_legitimate_checkpoint_fits_well_inside_the_cap()
    {

        int mutation = CovenantRecoveryCheckpointCodec
            .Encode(Reset(WidestEpochs) with { StartingRevision = long.MaxValue })
            .Length;

        int factory = CovenantRecoveryCheckpointCodec
            .Encode(FactoryReset(WidestEpochs) with { StartingRevision = long.MaxValue })
            .Length;

        Assert.True(mutation * 2 < CovenantRecoveryJsonContext.MaxCheckpointBytes);

        Assert.True(factory * 2 < CovenantRecoveryJsonContext.MaxCheckpointBytes);

    }

    /// <summary>
    /// Recovery reconstructs the identical owner from the launch's identity fields and nothing else.
    /// </summary>
    /// <remarks>
    /// The plan the launch also carries is varied across its whole admissible range here, and the
    /// owner has to come out the same every time. An owner that moved with the plan would let a
    /// retry whose plan had changed rebuild an owner matching the closed scope it has no right to
    /// adopt — the same defect the retired per-phase arm was written to prevent, now that the plan
    /// rather than the phase is what varies between two launches of one operation.
    /// </remarks>
    [Theory]
    [InlineData(1UL, 2UL, 3UL)]
    [InlineData(11UL, 22UL, 33UL)]
    [InlineData(9_007_199_254_740_993UL, 9_007_199_254_740_994UL, 9_007_199_254_740_995UL)]
    [InlineData((ulong)long.MaxValue - 3, (ulong)long.MaxValue - 2, (ulong)long.MaxValue - 1)]
    public void Every_admissible_plan_reconstructs_the_same_exclusive_owner(
        ulong accelerator,
        ulong reclamation,
        ulong envelope)
    {

        Result<CovenantExclusiveRecoveryOwner> owner =
            CovenantRecoveryCheckpointCodec.RecoveryOwner(
                Reset(new CovenantOfflineTransitionEpochsV1(accelerator, reclamation, envelope)));

        Assert.True(owner.IsSuccess);

        Assert.Equal(Operation, owner.Value.OperationId);

        Assert.Equal(CovenantExclusiveOperation.CovenantReset, owner.Value.Operation);

        Assert.Equal(new CovenantDigest(Convert.FromHexString(Effect)), owner.Value.EffectDigest);

        Assert.Equal(CovenantRecoveryCheckpointCodec.RecoveryOwner(Reset()).Value, owner.Value);

    }

    [Fact]
    public void A_factory_transition_launch_reconstructs_its_own_exclusive_owner()
    {

        Result<CovenantExclusiveRecoveryOwner> owner =
            CovenantRecoveryCheckpointCodec.RecoveryOwner(FactoryReset());

        Assert.True(owner.IsSuccess);

        Assert.Equal(
            CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
            owner.Value.Operation);

    }

    private static void AssertUnrecoverableCovenantLaunch(byte[] payload)
    {

        Result<CovenantOfflineTransitionLaunchV4> decoded =
            CovenantRecoveryCheckpointCodec.DecodeCovenantOfflineTransitionLaunch(payload);

        Assert.True(decoded.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, decoded.Error.Code);

    }

}
