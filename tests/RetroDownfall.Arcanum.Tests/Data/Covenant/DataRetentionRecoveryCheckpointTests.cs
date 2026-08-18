using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// Issue #118 — the V3 data-retention mutation checkpoint and the V1 factory-reset checkpoint.
/// </summary>
/// <remarks>
/// Both carry the only durable record of an interrupted Covenant erasure: the immutable server
/// operation identity, the canonical 32-byte effect digest, the exact operation code, and the phase.
/// Recovery reconstructs its exclusive owner from these three fields and nothing else, so a payload
/// that decoded with one of them altered would adopt a closed scope that belongs to a different
/// operation. Every failure is one code, because an operator told "wrong operation code" and one
/// told "unknown phase" does the same thing: look at the operation, not at the bytes.
/// </remarks>
public sealed class DataRetentionRecoveryCheckpointTests
{

    private static readonly Guid Operation = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private static readonly string Effect = new('a', 64);

    private static CovenantResetEffectArmV1 Arm(
        CovenantResetPhase phase = CovenantResetPhase.InventoryPrepared) =>
        new(
            Operation,
            Effect,
            CovenantExclusiveOperation.CovenantReset,
            phase);

    private static DataRetentionMutationCheckpointV3 Mutation(
        CovenantResetPhase phase = CovenantResetPhase.InventoryPrepared) =>
        new(
            DataRetentionMutationCheckpointV3.CurrentVersion,
            Subtype: "reset-memory",
            Target: "5",
            Arm(phase));

    private static DataRetentionFactoryResetCheckpointV1 FactoryReset(
        CovenantResetPhase phase = CovenantResetPhase.InventoryPrepared) =>
        new(
            DataRetentionFactoryResetCheckpointV1.CurrentVersion,
            Operation,
            Effect,
            CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
            phase);

    [Fact]
    public void The_two_checkpoint_versions_are_the_ones_the_registry_pins()
    {

        Assert.Equal(3, DataRetentionMutationCheckpointV3.CurrentVersion);

        Assert.Equal(1, DataRetentionFactoryResetCheckpointV1.CurrentVersion);

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
    public void A_v3_mutation_checkpoint_round_trips_from_every_phase(CovenantResetPhase phase)
    {

        Result<DataRetentionMutationCheckpointV3> decoded =
            CovenantRecoveryCheckpointCodec.DecodeDataRetentionMutation(
                CovenantRecoveryCheckpointCodec.Encode(Mutation(phase)));

        Assert.True(decoded.IsSuccess);

        Assert.Equal(Mutation(phase), decoded.Value);

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
    public void A_v1_factory_reset_checkpoint_round_trips_from_every_phase(CovenantResetPhase phase)
    {

        Result<DataRetentionFactoryResetCheckpointV1> decoded =
            CovenantRecoveryCheckpointCodec.DecodeDataRetentionFactoryReset(
                CovenantRecoveryCheckpointCodec.Encode(FactoryReset(phase)));

        Assert.True(decoded.IsSuccess);

        Assert.Equal(FactoryReset(phase), decoded.Value);

    }

    /// <summary>
    /// The arm is optional, and its absence is an ordinary retention mutation rather than a defect.
    /// </summary>
    [Fact]
    public void A_v3_checkpoint_without_a_covenant_arm_decodes_as_an_ordinary_mutation()
    {

        DataRetentionMutationCheckpointV3 ordinary = Mutation() with
        {
            Subtype = "delete-session",
            Target = "b9f0f0f0f0f04f0f8f0f0f0f0f0f0f0f",
            Covenant = null,
        };

        Result<DataRetentionMutationCheckpointV3> decoded =
            CovenantRecoveryCheckpointCodec.DecodeDataRetentionMutation(
                CovenantRecoveryCheckpointCodec.Encode(ordinary));

        Assert.True(decoded.IsSuccess);

        Assert.Null(decoded.Value.Covenant);

    }

    [Fact]
    public void Phases_travel_as_names_so_a_reordered_enum_cannot_silently_change_a_resume_point()
    {

        string json = Encoding.UTF8.GetString(
            CovenantRecoveryCheckpointCodec.Encode(Mutation(CovenantResetPhase.WalTruncated)));

        Assert.Contains("\"phase\":\"WalTruncated\"", json, StringComparison.Ordinal);

        Assert.DoesNotContain("\"phase\":5", json, StringComparison.Ordinal);

    }

    [Fact]
    public void The_operation_code_travels_as_a_name_too()
    {

        string json = Encoding.UTF8.GetString(CovenantRecoveryCheckpointCodec.Encode(Mutation()));

        Assert.Contains("\"operation\":\"CovenantReset\"", json, StringComparison.Ordinal);

    }

    [Fact]
    public void A_numeric_phase_is_refused()
    {

        byte[] payload = Encoding.UTF8.GetBytes(
            Encoding.UTF8
                .GetString(CovenantRecoveryCheckpointCodec.Encode(Mutation()))
                .Replace("\"phase\":\"InventoryPrepared\"", "\"phase\":1", StringComparison.Ordinal));

        AssertUnrecoverableMutation(payload);

    }

    [Fact]
    public void An_unknown_phase_name_is_refused()
    {

        byte[] payload = Encoding.UTF8.GetBytes(
            Encoding.UTF8
                .GetString(CovenantRecoveryCheckpointCodec.Encode(Mutation()))
                .Replace(
                    "\"phase\":\"InventoryPrepared\"",
                    "\"phase\":\"CanonicalErased\"",
                    StringComparison.Ordinal));

        AssertUnrecoverableMutation(payload);

    }

    [Fact]
    public void An_unknown_field_fails_recovery_rather_than_being_dropped()
    {

        string json = Encoding.UTF8.GetString(CovenantRecoveryCheckpointCodec.Encode(Mutation()));

        AssertUnrecoverableMutation(
            Encoding.UTF8.GetBytes(json.Insert(1, "\"unmappedInvariant\":true,")));

    }

    [Fact]
    public void A_future_version_discriminator_fails_recovery()
    {

        AssertUnrecoverableMutation(
            CovenantRecoveryCheckpointCodec.Encode(Mutation() with { Version = 4 }));

        Result<DataRetentionFactoryResetCheckpointV1> factory =
            CovenantRecoveryCheckpointCodec.DecodeDataRetentionFactoryReset(
                CovenantRecoveryCheckpointCodec.Encode(FactoryReset() with { Version = 2 }));

        Assert.True(factory.IsFailure);

    }

    /// <summary>
    /// A reset arm may name only <see cref="CovenantExclusiveOperation.CovenantReset"/>, and a
    /// factory-reset checkpoint only <see cref="CovenantExclusiveOperation.HealthyCatalogFactoryErasure"/>.
    /// Anything else would mint an exclusive owner for an operation that never closed admission.
    /// </summary>
    [Fact]
    public void A_foreign_operation_code_is_refused_on_both_checkpoints()
    {

        AssertUnrecoverableMutation(
            CovenantRecoveryCheckpointCodec.Encode(
                Mutation() with
                {
                    Covenant = Arm() with
                    {
                        Operation = CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
                    },
                }));

        Result<DataRetentionFactoryResetCheckpointV1> factory =
            CovenantRecoveryCheckpointCodec.DecodeDataRetentionFactoryReset(
                CovenantRecoveryCheckpointCodec.Encode(
                    FactoryReset() with { Operation = CovenantExclusiveOperation.CovenantReset }));

        Assert.True(factory.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, factory.Error.Code);

    }

    [Fact]
    public void An_empty_operation_identity_is_refused()
    {

        AssertUnrecoverableMutation(
            CovenantRecoveryCheckpointCodec.Encode(
                Mutation() with { Covenant = Arm() with { OperationId = Guid.Empty } }));

    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("NOTHEXNOTHEXNOTHEXNOTHEXNOTHEXNOTHEXNOTHEXNOTHEXNOTHEXNOTHEXNOTH")]
    public void An_effect_digest_that_is_not_thirty_two_canonical_bytes_is_refused(string digest)
    {

        AssertUnrecoverableMutation(
            CovenantRecoveryCheckpointCodec.Encode(
                Mutation() with { Covenant = Arm() with { EffectDigest = digest } }));

    }

    [Fact]
    public void An_uppercase_effect_digest_is_refused_so_one_effect_has_one_encoding()
    {

        AssertUnrecoverableMutation(
            CovenantRecoveryCheckpointCodec.Encode(
                Mutation() with { Covenant = Arm() with { EffectDigest = new string('A', 64) } }));

    }

    /// <summary>
    /// The oversized payload is deliberately well-formed and otherwise valid, so only the length
    /// guard can refuse it.
    /// </summary>
    /// <remarks>
    /// A blob of NUL bytes would fail JSON parsing whether or not the bound existed, which proves
    /// nothing about the bound. Recovery runs before readiness, so the one thing that must be true
    /// is that a hostile or corrupt payload cannot make it allocate in proportion to itself.
    /// </remarks>
    [Fact]
    public void An_oversized_but_otherwise_valid_payload_is_refused_before_it_is_parsed()
    {

        byte[] oversized = CovenantRecoveryCheckpointCodec.Encode(
            Mutation() with
            {
                Target = new string('7', CovenantRecoveryJsonContext.MaxCheckpointBytes),
                Covenant = null,
            });

        Assert.True(oversized.Length > CovenantRecoveryJsonContext.MaxCheckpointBytes);

        AssertUnrecoverableMutation(oversized);

        // The same payload one byte under the cap decodes, so the refusal above is the bound and
        // not some other validation rejecting a long target.
        byte[] admissible = CovenantRecoveryCheckpointCodec.Encode(
            Mutation() with
            {
                Target = new string('7', 64),
                Covenant = null,
            });

        Assert.True(admissible.Length <= CovenantRecoveryJsonContext.MaxCheckpointBytes);

        Assert.True(
            CovenantRecoveryCheckpointCodec.DecodeDataRetentionMutation(admissible).IsSuccess);

    }

    [Fact]
    public void An_empty_payload_is_refused()
    {

        AssertUnrecoverableMutation([]);

    }

    [Fact]
    public void Malformed_bytes_fail_as_a_typed_result_rather_than_an_escaping_exception()
    {

        AssertUnrecoverableMutation("not json"u8.ToArray());

        Result<DataRetentionFactoryResetCheckpointV1> factory =
            CovenantRecoveryCheckpointCodec.DecodeDataRetentionFactoryReset("not json"u8.ToArray());

        Assert.True(factory.IsFailure);

    }

    [Fact]
    public void The_largest_legitimate_checkpoint_fits_well_inside_the_cap()
    {

        int mutation = CovenantRecoveryCheckpointCodec
            .Encode(Mutation(CovenantResetPhase.ManagedArtifactsProcessed))
            .Length;

        int factory = CovenantRecoveryCheckpointCodec
            .Encode(FactoryReset(CovenantResetPhase.ManagedArtifactsProcessed))
            .Length;

        Assert.True(mutation * 2 < CovenantRecoveryJsonContext.MaxCheckpointBytes);

        Assert.True(factory * 2 < CovenantRecoveryJsonContext.MaxCheckpointBytes);

    }

    /// <summary>
    /// Recovery reconstructs the identical owner from the checkpoint and nothing else.
    /// </summary>
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
    public void Every_phase_reconstructs_the_same_exclusive_owner(CovenantResetPhase phase)
    {

        Result<CovenantExclusiveRecoveryOwner> owner =
            CovenantRecoveryCheckpointCodec.RecoveryOwner(Arm(phase));

        Assert.True(owner.IsSuccess);

        Assert.Equal(Operation, owner.Value.OperationId);

        Assert.Equal(CovenantExclusiveOperation.CovenantReset, owner.Value.Operation);

        Assert.Equal(new CovenantDigest(Convert.FromHexString(Effect)), owner.Value.EffectDigest);

        Assert.Equal(CovenantRecoveryCheckpointCodec.RecoveryOwner(Arm()).Value, owner.Value);

    }

    [Fact]
    public void A_factory_reset_checkpoint_reconstructs_its_own_exclusive_owner()
    {

        Result<CovenantExclusiveRecoveryOwner> owner =
            CovenantRecoveryCheckpointCodec.RecoveryOwner(FactoryReset());

        Assert.True(owner.IsSuccess);

        Assert.Equal(
            CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
            owner.Value.Operation);

    }

    private static void AssertUnrecoverableMutation(byte[] payload)
    {

        Result<DataRetentionMutationCheckpointV3> decoded =
            CovenantRecoveryCheckpointCodec.DecodeDataRetentionMutation(payload);

        Assert.True(decoded.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, decoded.Error.Code);

    }

}
