using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The pairing a Section enforces between a decision's kind and the placement that carries it.
/// </summary>
public sealed class CovenantTurnSectionTests
{
    [Fact]
    public void A_confirmed_section_refuses_a_decision_the_linker_marked_eligible_proposed()
    {
        CovenantSnapshotCandidate global = CovenantTask6Fixture.GlobalConfirmed(
            "response.style",
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G2,
            1,
            1);
        CovenantPlanCandidateDecision mismatched = new(
            global,
            CovenantPlanDecision.EligibleProposed,
            null,
            CovenantPlacement.GlobalConfirmed);

        Assert.Throws<ArgumentException>(() => CovenantTurnSection.Create(
            CovenantPlacement.GlobalConfirmed,
            [mismatched]));
    }

    [Fact]
    public void A_proposed_section_refuses_a_decision_the_linker_marked_eligible_confirmed()
    {
        CovenantSnapshotCandidate proposed = CovenantTask6Fixture.CampaignProposed(
            "tests.output",
            CovenantTask6Fixture.G3,
            CovenantTask6Fixture.G4,
            2,
            2,
            CovenantTask6Fixture.CampaignId);
        CovenantPlanCandidateDecision mismatched = new(
            proposed,
            CovenantPlanDecision.EligibleConfirmed,
            null,
            CovenantPlacement.CampaignProposed);

        Assert.Throws<ArgumentException>(() => CovenantTurnSection.Create(
            CovenantPlacement.CampaignProposed,
            [mismatched]));
    }

    [Fact]
    public void Each_placement_still_admits_the_one_decision_the_linker_pairs_with_it()
    {
        CovenantPlanCandidateDecision confirmed = new(
            CovenantTask6Fixture.GlobalConfirmed(
                "response.style",
                CovenantTask6Fixture.G1,
                CovenantTask6Fixture.G2,
                1,
                1),
            CovenantPlanDecision.EligibleConfirmed,
            null,
            CovenantPlacement.GlobalConfirmed);
        CovenantPlanCandidateDecision proposed = new(
            CovenantTask6Fixture.CampaignProposed(
                "tests.output",
                CovenantTask6Fixture.G3,
                CovenantTask6Fixture.G4,
                2,
                2,
                CovenantTask6Fixture.CampaignId),
            CovenantPlanDecision.EligibleProposed,
            null,
            CovenantPlacement.CampaignProposed);

        Assert.Single(CovenantTurnSection.Create(CovenantPlacement.GlobalConfirmed, [confirmed]).Candidates);
        Assert.Single(CovenantTurnSection.Create(CovenantPlacement.CampaignProposed, [proposed]).Candidates);
    }
}
