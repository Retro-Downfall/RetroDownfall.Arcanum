using System.Reflection;

using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// Issue #118 — the one durable phase vocabulary a Covenant reset and a healthy-catalog factory
/// erasure recover from.
/// </summary>
/// <remarks>
/// The defect these prevent is a recovery that believes a phase it never reached. The codes are
/// persisted, so a reordered member repoints an existing checkpoint at a different step, and a
/// checkpoint that could regress or skip would let a resumed erasure replace a dataset twice or
/// claim a storage-health proof nobody ran.
/// </remarks>
public sealed class CovenantResetPhaseMachineTests
{

    [Fact]
    public void Covenant_reset_phase_codes_are_literal_and_exhaustive()
    {

        Assert.Equal((byte)1, (byte)CovenantResetPhase.InventoryPrepared);

        Assert.Equal((byte)2, (byte)CovenantResetPhase.CanonicalApplied);

        Assert.Equal((byte)3, (byte)CovenantResetPhase.ManagedArtifactsProcessed);

        Assert.Equal((byte)4, (byte)CovenantResetPhase.HandlesClosed);

        Assert.Equal((byte)5, (byte)CovenantResetPhase.WalTruncated);

        Assert.Equal((byte)6, (byte)CovenantResetPhase.DatabaseCompacted);

        Assert.Equal((byte)7, (byte)CovenantResetPhase.AcceleratorInitialized);

        Assert.Equal((byte)8, (byte)CovenantResetPhase.FinalWalTruncated);

        Assert.Equal((byte)9, (byte)CovenantResetPhase.SidecarsVerified);

        Assert.Equal((byte)10, (byte)CovenantResetPhase.ReopenedVerified);

        Assert.Equal(10, Enum.GetValues<CovenantResetPhase>().Length);

    }

    [Fact]
    public void The_ordered_projection_is_the_declared_vocabulary_in_ascending_code_order()
    {

        Assert.Equal(
            [.. Enum.GetValues<CovenantResetPhase>().OrderBy(static phase => (byte)phase)],
            CovenantResetPhaseMachine.Ordered);

        Assert.Equal(CovenantResetPhase.InventoryPrepared, CovenantResetPhaseMachine.First);

        Assert.Equal(CovenantResetPhase.ReopenedVerified, CovenantResetPhaseMachine.Last);

    }

    [Fact]
    public void Zero_is_not_a_phase()
    {

        Assert.False(CovenantResetPhaseMachine.IsDeclared(default));

        Result declared = CovenantResetPhaseMachine.RequireDeclared(default);

        Assert.True(declared.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, declared.Error.Code);

    }

    [Theory]
    [InlineData(11)]
    [InlineData(99)]
    [InlineData(255)]
    public void An_unknown_phase_fails_closed(byte code)
    {

        Assert.False(CovenantResetPhaseMachine.IsDeclared((CovenantResetPhase)code));

        Assert.True(CovenantResetPhaseMachine.RequireDeclared((CovenantResetPhase)code).IsFailure);

    }

    [Fact]
    public void Every_declared_phase_advances_to_exactly_its_successor()
    {

        foreach (CovenantResetPhase phase in CovenantResetPhaseMachine.Ordered)
        {

            if (phase == CovenantResetPhaseMachine.Last)
            {

                continue;

            }

            Assert.True(
                CovenantResetPhaseMachine
                    .RequireAdvance(phase, (CovenantResetPhase)((byte)phase + 1))
                    .IsSuccess,
                phase.ToString());

        }

    }

    [Fact]
    public void A_skipped_phase_fails_closed()
    {

        Result skipped = CovenantResetPhaseMachine.RequireAdvance(
            CovenantResetPhase.InventoryPrepared,
            CovenantResetPhase.ManagedArtifactsProcessed);

        Assert.True(skipped.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, skipped.Error.Code);

    }

    [Fact]
    public void A_regressed_phase_fails_closed()
    {

        Result regressed = CovenantResetPhaseMachine.RequireAdvance(
            CovenantResetPhase.DatabaseCompacted,
            CovenantResetPhase.WalTruncated);

        Assert.True(regressed.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, regressed.Error.Code);

    }

    /// <summary>
    /// Re-saving the phase already recorded is not an advance. A resumed pass that re-ran a step it
    /// had already proved would look identical to one that ran it twice.
    /// </summary>
    [Fact]
    public void Standing_still_is_not_an_advance()
    {

        Assert.True(
            CovenantResetPhaseMachine
                .RequireAdvance(CovenantResetPhase.HandlesClosed, CovenantResetPhase.HandlesClosed)
                .IsFailure);

    }

    [Fact]
    public void Advancing_past_the_last_phase_fails_closed()
    {

        Assert.True(
            CovenantResetPhaseMachine
                .RequireAdvance(CovenantResetPhaseMachine.Last, (CovenantResetPhase)11)
                .IsFailure);

    }

    [Fact]
    public void Remaining_work_is_everything_after_the_recorded_phase()
    {

        Assert.Equal(
            [
                CovenantResetPhase.FinalWalTruncated,
                CovenantResetPhase.SidecarsVerified,
                CovenantResetPhase.ReopenedVerified,
            ],
            CovenantResetPhaseMachine.Remaining(CovenantResetPhase.AcceleratorInitialized));

        Assert.Empty(CovenantResetPhaseMachine.Remaining(CovenantResetPhaseMachine.Last));

        Assert.Equal(9, CovenantResetPhaseMachine.Remaining(CovenantResetPhaseMachine.First).Count);

    }

    /// <summary>
    /// No route DTO owns a second reset phase enum. One vocabulary, declared once: two enums that
    /// agreed on the day they were written would, at the first divergence, let a response describe a
    /// step the durable checkpoint never recorded.
    /// </summary>
    [Fact]
    public void No_second_enum_declares_the_reset_phase_vocabulary()
    {

        HashSet<string> vocabulary =
        [
            .. Enum.GetNames<CovenantResetPhase>(),
        ];

        string[] duplicates =
        [
            .. ShippingAssemblies()
                .SelectMany(static assembly => assembly.GetTypes())
                .Where(static type => type.IsEnum)
                .Where(type => vocabulary.SetEquals(Enum.GetNames(type)))
                .Select(static type => type.FullName!)
                .Where(static name => name != typeof(CovenantResetPhase).FullName)
                .OrderBy(static name => name, StringComparer.Ordinal),
        ];

        Assert.Empty(duplicates);

    }

    private static IEnumerable<Assembly> ShippingAssemblies() =>
    [
        typeof(CovenantResetPhase).Assembly,
        typeof(CovenantRecoveryJsonContext).Assembly,
        typeof(ArcanumJsonContext).Assembly,
        typeof(CliJsonContext).Assembly,
    ];

}
