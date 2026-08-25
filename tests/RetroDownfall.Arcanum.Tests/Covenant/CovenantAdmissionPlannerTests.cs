using System.Collections.Immutable;
using System.Text;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// Per-attempt admission over a reused plan: Confirmed all-or-fail, Proposed longest prefix (§10.13).
/// </summary>
public sealed class CovenantAdmissionPlannerTests
{

    [Fact]
    public void Plan_AdmitsEverythingWhenTheBudgetIsAmple()
    {
        CovenantTurnPlan plan = Plan(confirmed: 2, proposed: 3);

        CovenantAdmissionPlan admission = CovenantAdmissionPlanner.Plan(
            plan,
            10_000,
            ByteCost,
            FragmentCost);

        Assert.True(admission.ConfirmedAdmitted);
        Assert.Equal(3, admission.AdmittedProposedCount);
        Assert.Equal(0, admission.ProposedRemovals);
        Assert.All(
            admission.Candidates,
            static candidate => Assert.Equal(CovenantAdmissionDecision.Admitted, candidate.Decision));
    }

    [Fact]
    public void Plan_KeepsTheLongestProposedPrefixThatFitsAndPressuresTheSuffix()
    {
        CovenantTurnPlan plan = Plan(confirmed: 1, proposed: 3);
        ulong confirmedOnly = ByteCost(CovenantPromptContent.FromAdmittedPrefix(plan, true, 0));
        ulong onePropose = ByteCost(CovenantPromptContent.FromAdmittedPrefix(plan, true, 1));

        CovenantAdmissionPlan admission = CovenantAdmissionPlanner.Plan(
            plan,
            onePropose,
            ByteCost,
            FragmentCost);

        Assert.True(admission.ConfirmedAdmitted);
        Assert.Equal(1, admission.AdmittedProposedCount);
        Assert.Equal(2, admission.ProposedRemovals);
        Assert.True(confirmedOnly < onePropose, "The framed Proposed section costs something.");
        Assert.Equal(
            [
                CovenantAdmissionDecision.Admitted,
                CovenantAdmissionDecision.Admitted,
                CovenantAdmissionDecision.Pressured,
                CovenantAdmissionDecision.Pressured,
            ],
            admission.Candidates.Select(static candidate => candidate.Decision));
    }

    [Fact]
    public void Plan_RefusesEverythingWhenConfirmedCannotFit()
    {
        CovenantTurnPlan plan = Plan(confirmed: 2, proposed: 2);

        CovenantAdmissionPlan admission = CovenantAdmissionPlanner.Plan(
            plan,
            1,
            ByteCost,
            FragmentCost);

        Assert.False(admission.ConfirmedAdmitted);
        Assert.Equal(0, admission.AdmittedProposedCount);
        Assert.True(admission.AdmittedContent.IsEmpty);
        Assert.Equal(
            [
                CovenantAdmissionDecision.RequiredNoFit,
                CovenantAdmissionDecision.RequiredNoFit,
                CovenantAdmissionDecision.Pressured,
                CovenantAdmissionDecision.Pressured,
            ],
            admission.Candidates.Select(static candidate => candidate.Decision));
    }

    [Fact]
    public void Plan_ProducesAReceiptShapeTheAdmissionContractAccepts()
    {
        CovenantTurnPlan plan = Plan(confirmed: 1, proposed: 2);
        ulong budget = ByteCost(CovenantPromptContent.FromAdmittedPrefix(plan, true, 1));

        CovenantAdmissionPlan admission = CovenantAdmissionPlanner.Plan(
            plan,
            budget,
            ByteCost,
            FragmentCost);
        CovenantAdmissionReceipt receipt = new(
            plan,
            1,
            CovenantTask6Fixture.BranchId,
            1,
            null,
            CovenantTask6Fixture.ProviderCall(),
            budget,
            admission.EstimatedAdmittedTokens,
            admission.Candidates);

        Assert.Equal(admission.Candidates.AsEnumerable(), receipt.EligibleCandidates.AsEnumerable());
        Assert.Equal(
            admission.AdmittedContent.CampaignProposed,
            Text(receipt.AdmittedCampaignProposedSection.RenderedBytes));
        Assert.Equal(
            admission.AdmittedContent.GlobalConfirmed,
            Text(receipt.AdmittedGlobalConfirmedSection.RenderedBytes));
    }

    [Fact]
    public void Plan_ReusesOnePlanAcrossTwoAttemptsWithDifferentBudgets()
    {
        CovenantTurnPlan plan = Plan(confirmed: 1, proposed: 2);

        CovenantAdmissionPlan generous = CovenantAdmissionPlanner.Plan(plan, 10_000, ByteCost, FragmentCost);
        CovenantAdmissionPlan tight = CovenantAdmissionPlanner.Plan(
            plan,
            ByteCost(CovenantPromptContent.FromAdmittedPrefix(plan, true, 0)),
            ByteCost,
            FragmentCost);

        Assert.Equal(2, generous.AdmittedProposedCount);
        Assert.Equal(0, tight.AdmittedProposedCount);
        Assert.True(tight.ConfirmedAdmitted);
        Assert.Equal(generous.Candidates.Length, tight.Candidates.Length);
    }

    private static CovenantTurnPlan Plan(int confirmed, int proposed)
    {
        List<CovenantSnapshotCandidate> candidates = [];

        for (int index = 0; index < confirmed; index++)
        {
            candidates.Add(CovenantTask6Fixture.GlobalConfirmed(
                $"confirmed.{index}",
                CovenantTask6Fixture.GuidFor(100 + index),
                CovenantTask6Fixture.GuidFor(200 + index),
                (ulong)(index + 1),
                (byte)(index + 1)));
        }

        for (int index = 0; index < proposed; index++)
        {
            candidates.Add(CovenantTask6Fixture.CampaignProposed(
                $"proposed.{index}",
                CovenantTask6Fixture.GuidFor(300 + index),
                CovenantTask6Fixture.GuidFor(400 + index),
                (ulong)(confirmed + index + 1),
                (byte)(50 + index),
                CovenantTask6Fixture.CampaignId));
        }

        return new CovenantLinker()
            .Link(CovenantTask6Fixture.Snapshot(CovenantTask6Fixture.CampaignId, [.. candidates]))
            .Value;
    }

    /// <summary>A deterministic stand-in for a tokenizer: one "token" per rendered byte.</summary>
    private static ulong ByteCost(CovenantPromptContent content) =>
        (ulong)(content.GlobalConfirmed.Length
            + content.CampaignConfirmed.Length
            + content.CampaignProposed.Length);

    private static ulong FragmentCost(string fragment) =>
        (ulong)fragment.Length;

    private static string Text(ImmutableArray<byte> bytes) =>
        bytes.IsEmpty ? string.Empty : Encoding.UTF8.GetString(bytes.AsSpan());

}
