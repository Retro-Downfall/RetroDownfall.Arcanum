using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The two Proposed-block renderers, held byte-for-byte together.
/// </summary>
/// <remarks>
/// <see cref="CovenantCompiler.RenderProposedSection"/> reads the fence length persisted with each
/// compiled version, while the renderer that actually reaches a prompt rescans the frozen fragment
/// bytes. They are two independent answers to the same question, and the compiler's tests read as
/// proof of shipping behaviour only while the answers agree. A drift between them is a fence the
/// payload can close early, which is the whole reason the fence adapts at all.
/// </remarks>
public sealed class CovenantProposedFenceAgreementTests
{
    private readonly CovenantCompiler _compiler = new();

    [Theory]
    [InlineData("no ticks", 3)]
    [InlineData("one ` tick", 3)]
    [InlineData("two `` ticks", 3)]
    [InlineData("three ``` ticks", 4)]
    [InlineData("four ```` ticks", 5)]
    [InlineData("five ````` ticks", 6)]
    public void Both_Proposed_renderers_agree_on_the_bytes_and_the_fence(string authored, int expectedFence)
    {
        CovenantCompiledContent plain = _compiler.Compile("alpha", "plain");
        CovenantCompiledContent ticked = _compiler.Compile("beta", authored);

        string persisted = _compiler.RenderProposedSection([plain, ticked]);
        CovenantTurnSection shipped = CovenantTurnSection.Create(
            CovenantPlacement.CampaignProposed,
            [Decision("alpha", plain), Decision("beta", ticked)]);

        Assert.Equal(persisted, Encoding.UTF8.GetString(shipped.RenderedBytes.AsSpan()));
        Assert.Equal(expectedFence, Math.Max(plain.RequiredFenceLength, ticked.RequiredFenceLength));
        Assert.Equal(expectedFence, OpeningFenceLength(persisted));
    }

    private static int OpeningFenceLength(string section)
    {
        int length = 0;

        while (length < section.Length && section[length] == '`')
        {
            length++;
        }

        return length;
    }

    private static CovenantPlanCandidateDecision Decision(string key, CovenantCompiledContent compiled) =>
        new(
            CovenantTask6Fixture.CreateCandidate(
                key,
                Guid.NewGuid(),
                Guid.NewGuid(),
                1,
                CovenantScope.Campaign,
                CovenantTask6Fixture.CampaignId,
                CovenantLane.Proposed,
                CovenantOperation.Set,
                CovenantOrigin.AgentProposed,
                CovenantCompiler.CompilerPolicyVersion,
                0,
                CovenantSnapshotCandidateIntegrity.Verified,
                compiledFragment: [.. Encoding.UTF8.GetBytes(compiled.Fragment)]),
            CovenantPlanDecision.EligibleProposed,
            null,
            CovenantPlacement.CampaignProposed);
}
