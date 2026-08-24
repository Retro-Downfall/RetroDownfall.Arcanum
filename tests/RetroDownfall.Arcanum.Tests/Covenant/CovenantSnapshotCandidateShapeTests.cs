using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The shape a verified compiled fragment must have before it is allowed inside a rendered fence.
/// </summary>
public sealed class CovenantSnapshotCandidateShapeTests
{
    [Fact]
    public void A_verified_fragment_carrying_an_interior_lf_is_refused()
    {
        ImmutableArray<byte> interiorLf = [.. "- tests.output: \"a\n``` still me\"\n"u8];

        Assert.Throws<ArgumentException>(() => Proposed("tests.output", interiorLf));
    }

    [Fact]
    public void A_verified_fragment_that_does_not_begin_with_the_bullet_marker_is_refused()
    {
        ImmutableArray<byte> unmarked = [.. "``` tests.output: \"a\"\n"u8];

        Assert.Throws<ArgumentException>(() => Proposed("tests.output", unmarked));
    }

    [Fact]
    public void A_compiler_shaped_fragment_is_still_accepted()
    {
        ImmutableArray<byte> compiled = [.. "- tests.output: \"a\"\n"u8];

        Assert.Equal(
            compiled.AsSpan().ToArray(),
            Proposed("tests.output", compiled).CompiledFragment.AsSpan().ToArray());
    }

    private static CovenantSnapshotCandidate Proposed(string key, ImmutableArray<byte> compiledFragment) =>
        CovenantTask6Fixture.CreateCandidate(
            key,
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G2,
            1,
            CovenantScope.Campaign,
            CovenantTask6Fixture.CampaignId,
            CovenantLane.Proposed,
            CovenantOperation.Set,
            CovenantOrigin.AgentProposed,
            CovenantCompiler.CompilerPolicyVersion,
            0,
            CovenantSnapshotCandidateIntegrity.Verified,
            compiledFragment: compiledFragment);
}
