using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// What a scope mask does to the turn that would otherwise have carried the Global preference.
/// </summary>
/// <remarks>
/// Every mask here is applied through the production curation path, and every plan is produced by the
/// production loader and linker over what that path wrote. Nothing is seeded and nothing is asserted
/// about a snapshot the suite built by hand.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class CovenantMaskPlanningTests
{

    private static CancellationToken Token => CancellationToken.None;

    private const string Key = "preference.builds";

    private static readonly Guid CampaignOne = CovenantOperationGateFixture.CampaignOne;

    private static readonly Guid CampaignTwo = new("B0000000-0000-4000-8000-000000000002");

    [Fact]
    public async Task A_masked_Global_key_reaches_no_turn_in_the_masking_Campaign()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.SetAsync(CovenantScope.Global, null, Key, "Build from the root.", Token);

        Assert.Contains(Key, await EligibleKeysAsync(harness, CampaignOne));

        Result<CovenantCurationResultDto> masked = await harness.CurateAsync(
            CovenantCurationKind.Mask,
            CovenantScope.Campaign,
            CampaignOne,
            Key,
            Token);

        Assert.True(masked.IsSuccess, masked.IsFailure ? masked.Error.Message : string.Empty);

        Assert.DoesNotContain(Key, await EligibleKeysAsync(harness, CampaignOne));

    }

    [Fact]
    public async Task A_masked_Global_key_still_reaches_every_other_Campaign()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.AddCampaignAsync(CampaignTwo, Token);

        await harness.SetAsync(CovenantScope.Global, null, Key, "Build from the root.", Token);

        _ = await harness.CurateAsync(
            CovenantCurationKind.Mask,
            CovenantScope.Campaign,
            CampaignOne,
            Key,
            Token);

        Assert.DoesNotContain(Key, await EligibleKeysAsync(harness, CampaignOne));

        Assert.Contains(Key, await EligibleKeysAsync(harness, CampaignTwo));

    }

    /// <summary>
    /// The ratified rule: a mask suppresses the Global candidate and nothing else, so a Campaign that
    /// later writes its own value for the key gets it. The alternative makes a later write silently
    /// inert, which is the one outcome an operator could not diagnose.
    /// </summary>
    [Fact]
    public async Task A_Campaign_that_masks_a_key_and_then_sets_its_own_gets_its_own_value()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.SetAsync(CovenantScope.Global, null, Key, "Build from the root.", Token);

        _ = await harness.CurateAsync(
            CovenantCurationKind.Mask,
            CovenantScope.Campaign,
            CampaignOne,
            Key,
            Token);

        await harness.SetAsync(CovenantScope.Campaign, CampaignOne, Key, "Build from tools.", Token);

        Assert.Contains(Key, await EligibleKeysAsync(harness, CampaignOne));

    }

    /// <summary>
    /// A shadow names the entry that replaced it. A mask names nothing, so folding the two together
    /// would tell an operator their Global preference had been superseded by content that does not
    /// exist.
    /// </summary>
    [Fact]
    public async Task A_masked_candidate_is_reported_as_masked_rather_than_shadowed()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.SetAsync(CovenantScope.Global, null, Key, "Build from the root.", Token);

        _ = await harness.CurateAsync(
            CovenantCurationKind.Mask,
            CovenantScope.Campaign,
            CampaignOne,
            Key,
            Token);

        CovenantTurnPlan plan = await PlanAsync(harness, CampaignOne);

        CovenantPlanCandidateDecision decision = Assert.Single(
            plan.Decisions.Where(candidate =>
                string.Equals(candidate.Candidate.NormalizedKey.Value, Key, StringComparison.Ordinal)));

        Assert.Equal(CovenantPlanDecision.Masked, decision.Decision);

        Assert.Null(decision.ShadowingVersionId);

    }

    /// <summary>
    /// Two snapshots holding identical candidates under different masks are distinguishable where it
    /// matters: the plan digest, which is what every staleness comparison downstream keys on.
    /// </summary>
    /// <remarks>
    /// The snapshot digest deliberately does not move. It names the content the turn read, and a mask is
    /// a filter over that content rather than part of it — so the mask's whole effect lands on the
    /// decisions, and the decisions are digested.
    /// </remarks>
    [Fact]
    public async Task A_mask_changes_the_plan_digest_although_the_candidates_are_identical()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.SetAsync(CovenantScope.Global, null, Key, "Build from the root.", Token);

        CovenantTurnPlan before = await PlanAsync(harness, CampaignOne);

        _ = await harness.CurateAsync(
            CovenantCurationKind.Mask,
            CovenantScope.Campaign,
            CampaignOne,
            Key,
            Token);

        CovenantTurnPlan after = await PlanAsync(harness, CampaignOne);

        Assert.Equal(
            before.Snapshot.Candidates.Select(static candidate => candidate.VersionId),
            after.Snapshot.Candidates.Select(static candidate => candidate.VersionId));

        Assert.Equal(before.Snapshot.Digest, after.Snapshot.Digest);

        Assert.NotEqual(before.Digest, after.Digest);

    }

    /// <summary>
    /// A masked Global entry is not effective content, so an agent proposal for the same key competes
    /// with nothing and stays eligible instead of being demoted to review-only.
    /// </summary>
    /// <remarks>
    /// The Proposed head is seeded rather than proposed, because the agent path that writes one is not
    /// reachable from this suite. What is asserted is the linker's decision about it, not its existence,
    /// and the mask it is weighed against was applied through the production curation path. The
    /// end-to-end reachability of this pairing is covered where a real proposal exists.
    ///
    /// <para>Without this the eligibility filter is dead: removing it leaves every other mask test green,
    /// because the reported decision comes from a different branch.</para>
    /// </remarks>
    [Fact]
    public async Task An_agent_proposal_for_a_masked_key_stays_eligible_rather_than_review_only()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.SetAsync(CovenantScope.Global, null, Key, "Build from the root.", Token);

        _ = await harness.Fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            Key,
            CovenantLane.Proposed,
            CovenantOperation.Set,
            "Build from tools, the model suggests.",
            Token);

        // While the Global entry applies here, the proposal is review-only beside it.
        Assert.Equal(CovenantPlanDecision.ReviewOnly, await ProposedDecisionAsync(harness));

        _ = await harness.CurateAsync(
            CovenantCurationKind.Mask,
            CovenantScope.Campaign,
            CampaignOne,
            Key,
            Token);

        Assert.Equal(CovenantPlanDecision.EligibleProposed, await ProposedDecisionAsync(harness));

    }

    private static async Task<CovenantPlanDecision> ProposedDecisionAsync(CovenantServiceHarness harness)
    {

        CovenantTurnPlan plan = await PlanAsync(harness, CampaignOne);

        return Assert.Single(
            plan.Decisions.Where(static decision =>
                decision.Candidate.Lane == CovenantLane.Proposed)).Decision;

    }

    private static async Task<ImmutableArray<string>> EligibleKeysAsync(
        CovenantServiceHarness harness,
        Guid campaignId)
    {

        CovenantTurnPlan plan = await PlanAsync(harness, campaignId);

        return
        [
            .. plan.Decisions
                .Where(static decision => decision.Decision is CovenantPlanDecision.EligibleConfirmed)
                .Select(static decision => decision.Candidate.NormalizedKey.Value),
        ];

    }

    private static async Task<CovenantTurnPlan> PlanAsync(CovenantServiceHarness harness, Guid campaignId)
    {

        Result<CovenantTurnPlan> plan = new CovenantLinker().Link(await SnapshotAsync(harness, campaignId));

        Assert.True(plan.IsSuccess, plan.IsFailure ? plan.Error.Message : string.Empty);

        return plan.Value;

    }

    private static async Task<CovenantTurnSnapshot> SnapshotAsync(
        CovenantServiceHarness harness,
        Guid campaignId)
    {

        await using ICovenantSnapshotReadLease read =
            (await harness.Gate.AcquireReadAsync(CovenantOperationScope.ForCampaign(campaignId), Token)).Value;

        Result<CovenantTurnSnapshot> snapshot = await harness.Fixture.Store.ReadTurnSnapshotAsync(
            CovenantCanonicalFixture.CampaignContext(campaignId),
            read,
            Token);

        Assert.True(snapshot.IsSuccess, snapshot.IsFailure ? snapshot.Error.Message : string.Empty);

        return snapshot.Value;

    }

}
