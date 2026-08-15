using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Tests.Covenant;

public sealed class CovenantLinkerTests
{
    [Fact]
    public void Global_only_fallback_is_eligible_and_key_ordered()
    {
        CovenantTurnSnapshot snapshot = CovenantTask6Fixture.Snapshot(
            null,
            CovenantTask6Fixture.GlobalConfirmed("z.key", CovenantTask6Fixture.G3, CovenantTask6Fixture.G4, 2, 2),
            CovenantTask6Fixture.GlobalConfirmed("a.key", CovenantTask6Fixture.G1, CovenantTask6Fixture.G2, 1, 1));

        CovenantTurnPlan plan = Link(snapshot);

        Assert.Equal(["a.key", "z.key"], Keys(plan.Decisions));
        Assert.All(plan.Decisions, decision => Assert.Equal(CovenantPlanDecision.EligibleConfirmed, decision.Decision));
        Assert.Equal("- a.key: \"a.key\"\n- z.key: \"z.key\"\n", Text(plan.GlobalConfirmedSection.RenderedBytes));
        Assert.Empty(plan.CampaignConfirmedSection.Candidates);
        Assert.Empty(plan.CampaignProposedSection.Candidates);
    }

    [Fact]
    public void Campaign_confirmed_shadows_only_its_matching_global_key()
    {
        CovenantSnapshotCandidate globalShadowed = CovenantTask6Fixture.GlobalConfirmed(
            "response.style",
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G2,
            1,
            1);
        CovenantSnapshotCandidate globalFallback = CovenantTask6Fixture.GlobalConfirmed(
            "fallback.only",
            CovenantTask6Fixture.G3,
            CovenantTask6Fixture.G4,
            2,
            2);
        CovenantSnapshotCandidate campaign = CovenantTask6Fixture.CampaignConfirmed(
            "response.style",
            CovenantTask6Fixture.G5,
            CovenantTask6Fixture.G6,
            3,
            3,
            CovenantTask6Fixture.CampaignId);
        CovenantTurnSnapshot snapshot = CovenantTask6Fixture.Snapshot(
            CovenantTask6Fixture.CampaignId,
            campaign,
            globalShadowed,
            globalFallback);

        CovenantTurnPlan plan = Link(snapshot);
        CovenantPlanCandidateDecision shadowed = Assert.Single(
            plan.Decisions,
            decision => decision.Candidate == globalShadowed);

        Assert.Equal(CovenantPlanDecision.Shadowed, shadowed.Decision);
        Assert.Equal(campaign.VersionId, shadowed.ShadowingVersionId);
        Assert.Equal(CovenantPlacement.GlobalConfirmed, shadowed.Placement);
        Assert.Equal([globalFallback], plan.GlobalConfirmedSection.Candidates.Select(static decision => decision.Candidate));
        Assert.Equal([campaign], plan.CampaignConfirmedSection.Candidates.Select(static decision => decision.Candidate));
    }

    [Fact]
    public void Confirmed_and_proposed_lanes_resolve_independently_and_same_key_proposed_is_review_only()
    {
        CovenantSnapshotCandidate confirmed = CovenantTask6Fixture.CampaignConfirmed(
            "same.key",
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G2,
            1,
            1,
            CovenantTask6Fixture.CampaignId);
        CovenantSnapshotCandidate proposedReview = CovenantTask6Fixture.CampaignProposed(
            "same.key",
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G4,
            2,
            2,
            CovenantTask6Fixture.CampaignId);
        CovenantSnapshotCandidate proposedEligible = CovenantTask6Fixture.CampaignProposed(
            "new.key",
            CovenantTask6Fixture.G5,
            CovenantTask6Fixture.G6,
            3,
            3,
            CovenantTask6Fixture.CampaignId);

        CovenantTurnPlan plan = Link(CovenantTask6Fixture.Snapshot(
            CovenantTask6Fixture.CampaignId,
            proposedReview,
            proposedEligible,
            confirmed));

        Assert.Equal(CovenantPlanDecision.EligibleConfirmed, DecisionFor(plan, confirmed).Decision);
        Assert.Equal(CovenantPlanDecision.ReviewOnly, DecisionFor(plan, proposedReview).Decision);
        Assert.Equal(confirmed.EntryId, proposedReview.EntryId);
        Assert.Equal(CovenantPlacement.CampaignProposed, DecisionFor(plan, proposedReview).Placement);
        Assert.Equal(CovenantPlanDecision.EligibleProposed, DecisionFor(plan, proposedEligible).Decision);
        Assert.Equal([proposedEligible], plan.CampaignProposedSection.Candidates.Select(static decision => decision.Candidate));
    }

    [Fact]
    public void Quarantined_and_invalid_candidates_remain_typed_exclusions_with_scope_derived_placement()
    {
        CovenantSnapshotCandidate quarantined = CovenantTask6Fixture.CampaignProposed(
            "quarantined.key",
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G2,
            1,
            1,
            CovenantTask6Fixture.CampaignId,
            CovenantSnapshotCandidateIntegrity.Quarantined);
        CovenantSnapshotCandidate invalid = CovenantTask6Fixture.CampaignProposed(
            "invalid.key",
            CovenantTask6Fixture.G3,
            CovenantTask6Fixture.G4,
            2,
            2,
            CovenantTask6Fixture.CampaignId,
            CovenantSnapshotCandidateIntegrity.Invalid);
        CovenantSnapshotCandidate eligible = CovenantTask6Fixture.CampaignProposed(
            "eligible.key",
            CovenantTask6Fixture.G5,
            CovenantTask6Fixture.G6,
            3,
            3,
            CovenantTask6Fixture.CampaignId);

        CovenantTurnPlan plan = Link(CovenantTask6Fixture.Snapshot(
            CovenantTask6Fixture.CampaignId,
            quarantined,
            invalid,
            eligible));

        Assert.Equal(CovenantPlanDecision.Quarantined, DecisionFor(plan, quarantined).Decision);
        Assert.Equal(CovenantPlacement.CampaignProposed, DecisionFor(plan, quarantined).Placement);
        Assert.Equal(CovenantPlanDecision.Invalid, DecisionFor(plan, invalid).Decision);
        Assert.Equal(CovenantPlacement.CampaignProposed, DecisionFor(plan, invalid).Placement);
        Assert.Equal([eligible], plan.EligibleDecisions.Select(static decision => decision.Candidate));
    }

    [Fact]
    public void Damaged_proposed_bytes_remain_representable_as_typed_exclusions_but_verified_bytes_fail()
    {
        ImmutableArray<byte> malformedUtf8 = [(byte)0xff, (byte)'\n'];
        ImmutableArray<byte> empty = [];
        ImmutableArray<byte> missingLf = [.. "not-terminated"u8];
        CovenantSnapshotCandidate quarantined = CovenantTask6Fixture.CreateCandidate(
            "excluded.malformed",
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G2,
            1,
            CovenantScope.Campaign,
            CovenantTask6Fixture.CampaignId,
            CovenantLane.Proposed,
            CovenantOperation.Set,
            CovenantOrigin.AgentProposed,
            1,
            0,
            CovenantSnapshotCandidateIntegrity.Quarantined,
            compiledFragment: malformedUtf8);
        CovenantSnapshotCandidate invalidEmpty = CovenantTask6Fixture.CreateCandidate(
            "excluded.empty",
            CovenantTask6Fixture.G3,
            CovenantTask6Fixture.G4,
            2,
            CovenantScope.Campaign,
            CovenantTask6Fixture.CampaignId,
            CovenantLane.Proposed,
            CovenantOperation.Set,
            CovenantOrigin.AgentProposed,
            1,
            0,
            CovenantSnapshotCandidateIntegrity.Invalid,
            compiledFragment: empty);
        CovenantSnapshotCandidate invalidMissingLf = CovenantTask6Fixture.CreateCandidate(
            "excluded.nonlf",
            CovenantTask6Fixture.G5,
            CovenantTask6Fixture.G6,
            3,
            CovenantScope.Campaign,
            CovenantTask6Fixture.CampaignId,
            CovenantLane.Proposed,
            CovenantOperation.Set,
            CovenantOrigin.AgentProposed,
            1,
            0,
            CovenantSnapshotCandidateIntegrity.Invalid,
            compiledFragment: missingLf);

        CovenantTurnPlan plan = Link(CovenantTask6Fixture.Snapshot(
            CovenantTask6Fixture.CampaignId,
            invalidMissingLf,
            quarantined,
            invalidEmpty));

        Assert.Equal(
            [CovenantPlanDecision.Invalid, CovenantPlanDecision.Quarantined, CovenantPlanDecision.Invalid],
            plan.Decisions.Select(static decision => decision.Decision));
        Assert.Empty(plan.CampaignProposedSection.Candidates);
        Assert.Empty(plan.CampaignProposedSection.RenderedBytes);
        Assert.Throws<ArgumentException>(() => CovenantTask6Fixture.CreateCandidate(
            "verified.malformed",
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G2,
            1,
            CovenantScope.Campaign,
            CovenantTask6Fixture.CampaignId,
            CovenantLane.Proposed,
            CovenantOperation.Set,
            CovenantOrigin.AgentProposed,
            1,
            0,
            CovenantSnapshotCandidateIntegrity.Verified,
            compiledFragment: malformedUtf8));
        Assert.Throws<ArgumentException>(() => CovenantTask6Fixture.CreateCandidate(
            "verified.empty",
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G2,
            1,
            CovenantScope.Campaign,
            CovenantTask6Fixture.CampaignId,
            CovenantLane.Proposed,
            CovenantOperation.Set,
            CovenantOrigin.AgentProposed,
            1,
            0,
            CovenantSnapshotCandidateIntegrity.Verified,
            compiledFragment: empty));
        Assert.Throws<ArgumentException>(() => CovenantTask6Fixture.CreateCandidate(
            "verified.nonlf",
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G2,
            1,
            CovenantScope.Campaign,
            CovenantTask6Fixture.CampaignId,
            CovenantLane.Proposed,
            CovenantOperation.Set,
            CovenantOrigin.AgentProposed,
            1,
            0,
            CovenantSnapshotCandidateIntegrity.Verified,
            compiledFragment: missingLf));
    }

    [Fact]
    public void Live_snapshot_rejects_retired_heads_instead_of_linking_them()
    {
        CovenantSnapshotCandidate retired = CovenantTask6Fixture.CreateCandidate(
            "retired.key",
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G2,
            1,
            CovenantScope.Global,
            null,
            CovenantLane.Confirmed,
            CovenantOperation.Retire,
            CovenantOrigin.Operator,
            1,
            1,
            CovenantSnapshotCandidateIntegrity.Verified);

        Assert.Throws<ArgumentException>(() => CovenantTask6Fixture.Snapshot(null, retired));
    }

    [Fact]
    public void Snapshot_rejects_duplicate_scoped_keys_with_distinct_physical_entry_heads()
    {
        CovenantSnapshotCandidate firstGlobal = CovenantTask6Fixture.GlobalConfirmed(
            "duplicate.key",
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G2,
            1,
            1);
        CovenantSnapshotCandidate secondGlobal = CovenantTask6Fixture.GlobalConfirmed(
            "duplicate.key",
            CovenantTask6Fixture.G3,
            CovenantTask6Fixture.G4,
            2,
            2);
        CovenantSnapshotCandidate firstCampaign = CovenantTask6Fixture.CampaignConfirmed(
            "duplicate.key",
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G2,
            1,
            1,
            CovenantTask6Fixture.CampaignId);
        CovenantSnapshotCandidate secondCampaign = CovenantTask6Fixture.CampaignConfirmed(
            "duplicate.key",
            CovenantTask6Fixture.G3,
            CovenantTask6Fixture.G4,
            2,
            2,
            CovenantTask6Fixture.CampaignId);

        Assert.Throws<ArgumentException>(() => CovenantTask6Fixture.Snapshot(
            null,
            firstGlobal,
            secondGlobal));
        Assert.Throws<ArgumentException>(() => CovenantTask6Fixture.Snapshot(
            CovenantTask6Fixture.CampaignId,
            firstCampaign,
            secondCampaign));
    }

    [Fact]
    public void Snapshot_rejects_one_entry_identity_with_inconsistent_scope_campaign_or_key_metadata()
    {
        Guid otherCampaign = CovenantTask6Fixture.GuidFor(900);
        CovenantSnapshotCandidate global = CovenantTask6Fixture.GlobalConfirmed(
            "identity.key",
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G2,
            1,
            1);
        CovenantSnapshotCandidate campaignProposed = CovenantTask6Fixture.CampaignProposed(
            "identity.key",
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G3,
            2,
            2,
            CovenantTask6Fixture.CampaignId);
        CovenantSnapshotCandidate campaignConfirmed = CovenantTask6Fixture.CampaignConfirmed(
            "identity.key",
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G2,
            1,
            1,
            CovenantTask6Fixture.CampaignId);
        CovenantSnapshotCandidate otherCampaignProposed = CovenantTask6Fixture.CampaignProposed(
            "identity.key",
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G3,
            2,
            2,
            otherCampaign);
        CovenantSnapshotCandidate otherKeyProposed = CovenantTask6Fixture.CampaignProposed(
            "identity.other",
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G3,
            2,
            2,
            CovenantTask6Fixture.CampaignId);

        Assert.Throws<ArgumentException>(() => CovenantTask6Fixture.Snapshot(
            CovenantTask6Fixture.CampaignId,
            global,
            campaignProposed));
        Assert.Throws<ArgumentException>(() => CovenantTask6Fixture.Snapshot(
            CovenantTask6Fixture.CampaignId,
            campaignConfirmed,
            otherCampaignProposed));
        Assert.Throws<ArgumentException>(() => CovenantTask6Fixture.Snapshot(
            CovenantTask6Fixture.CampaignId,
            campaignConfirmed,
            otherKeyProposed));
    }

    [Fact]
    public void Randomized_storage_order_produces_stable_canonical_decision_and_section_order()
    {
        CovenantSnapshotCandidate[] candidates =
        [
            .. Enumerable.Range(0, 12).Select(index => CovenantTask6Fixture.GlobalConfirmed(
                $"global.{index:D2}",
                CovenantTask6Fixture.GuidFor(20 + (index * 2)),
                CovenantTask6Fixture.GuidFor(21 + (index * 2)),
                (ulong)(index + 1),
                (byte)(index + 1))),
            .. Enumerable.Range(0, 8).Select(index => CovenantTask6Fixture.CampaignProposed(
                $"proposed.{index:D2}",
                CovenantTask6Fixture.GuidFor(60 + (index * 2)),
                CovenantTask6Fixture.GuidFor(61 + (index * 2)),
                (ulong)(index + 20),
                (byte)(index + 20),
                CovenantTask6Fixture.CampaignId))
        ];
        string[]? expectedDecisions = null;
        string? expectedGlobal = null;
        string? expectedProposed = null;

        for (int seed = 17; seed < 49; seed++)
        {
            CovenantSnapshotCandidate[] shuffled = candidates.ToArray();
            Shuffle(shuffled, new Random(seed));
            CovenantTurnPlan plan = Link(CovenantTask6Fixture.Snapshot(CovenantTask6Fixture.CampaignId, shuffled));
            string[] actualDecisions = Keys(plan.Decisions);
            string actualGlobal = Text(plan.GlobalConfirmedSection.RenderedBytes);
            string actualProposed = Text(plan.CampaignProposedSection.RenderedBytes);

            expectedDecisions ??= actualDecisions;
            expectedGlobal ??= actualGlobal;
            expectedProposed ??= actualProposed;

            Assert.Equal(expectedDecisions, actualDecisions);
            Assert.Equal(expectedGlobal, actualGlobal);
            Assert.Equal(expectedProposed, actualProposed);
        }
    }

    [Fact]
    public void Canonical_order_uses_normalized_key_then_raw_rfc_guid_bytes()
    {
        Guid rawEarlier = Guid.Parse("00000001-0000-0000-0000-000000000000");
        Guid rawLater = Guid.Parse("01000000-0000-0000-0000-000000000000");
        Guid otherCampaign = CovenantTask6Fixture.GuidFor(901);
        CovenantSnapshotCandidate later = CovenantTask6Fixture.CampaignConfirmed(
            "same.key",
            rawLater,
            CovenantTask6Fixture.G2,
            2,
            2,
            otherCampaign);
        CovenantSnapshotCandidate earlier = CovenantTask6Fixture.CampaignConfirmed(
            "same.key",
            rawEarlier,
            CovenantTask6Fixture.G1,
            1,
            1,
            CovenantTask6Fixture.CampaignId);
        string[] byteOrderedKeys = ["aa", "a_", "a0", "a.", "a-"];
        CovenantSnapshotCandidate[] byteOrdered = byteOrderedKeys
            .Select((key, index) => CovenantTask6Fixture.GlobalConfirmed(
                key,
                CovenantTask6Fixture.GuidFor(100 + (index * 2)),
                CovenantTask6Fixture.GuidFor(101 + (index * 2)),
                (ulong)(index + 10),
                (byte)(index + 10)))
            .ToArray();

        CovenantTurnPlan plan = Link(CovenantTask6Fixture.Snapshot(
            CovenantTask6Fixture.CampaignId,
            [later, .. byteOrdered, earlier]));

        Assert.Equal(
            ["a-", "a.", "a0", "a_", "aa", "same.key", "same.key"],
            plan.Decisions.Select(static decision => decision.Candidate.NormalizedKey.Value));
        Assert.Equal(
            [rawEarlier, rawLater],
            plan.Decisions
                .Where(static decision => decision.Candidate.NormalizedKey.Value == "same.key")
                .Select(static decision => decision.Candidate.EntryId));
        Assert.Equal(
            ["a-", "a.", "a0", "a_", "aa"],
            plan.GlobalConfirmedSection.Candidates.Select(static decision => decision.Candidate.NormalizedKey.Value));
        Assert.Equal([earlier], plan.CampaignConfirmedSection.Candidates.Select(static decision => decision.Candidate));
    }

    [Fact]
    public void Proposed_section_uses_a_fence_longer_than_every_compiled_backtick_run()
    {
        CovenantSnapshotCandidate candidate = CovenantTask6Fixture.CreateCandidate(
            "proposed.fence",
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G2,
            1,
            CovenantScope.Campaign,
            CovenantTask6Fixture.CampaignId,
            CovenantLane.Proposed,
            CovenantOperation.Set,
            CovenantOrigin.AgentProposed,
            1,
            0,
            CovenantSnapshotCandidateIntegrity.Verified,
            compiledFragment: [.. "- proposed.fence: \"```\"\n"u8]);

        CovenantTurnPlan plan = Link(CovenantTask6Fixture.Snapshot(CovenantTask6Fixture.CampaignId, candidate));

        Assert.Equal("````text\n- proposed.fence: \"```\"\n````\n", Text(plan.CampaignProposedSection.RenderedBytes));
    }

    [Fact]
    public void Oversized_sections_fail_before_allocating_a_rendered_payload_buffer()
    {
        ImmutableArray<byte> oversizedFragment = OversizedFragment();
        CovenantSnapshotCandidate confirmed = CovenantTask6Fixture.CreateCandidate(
            "oversized.confirmed",
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G2,
            1,
            CovenantScope.Global,
            null,
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            CovenantOrigin.Operator,
            1,
            0,
            CovenantSnapshotCandidateIntegrity.Verified,
            compiledFragment: oversizedFragment);
        CovenantSnapshotCandidate proposed = CovenantTask6Fixture.CreateCandidate(
            "oversized.proposed",
            CovenantTask6Fixture.G3,
            CovenantTask6Fixture.G4,
            2,
            CovenantScope.Campaign,
            CovenantTask6Fixture.CampaignId,
            CovenantLane.Proposed,
            CovenantOperation.Set,
            CovenantOrigin.AgentProposed,
            1,
            0,
            CovenantSnapshotCandidateIntegrity.Verified,
            compiledFragment: oversizedFragment);
        CovenantTurnSnapshot confirmedSnapshot = CovenantTask6Fixture.Snapshot(null, confirmed);
        CovenantTurnSnapshot proposedSnapshot = CovenantTask6Fixture.Snapshot(
            CovenantTask6Fixture.CampaignId,
            proposed);
        CovenantLinker linker = new();

        long confirmedAllocation = MeasureRejectedLinkAllocation(() => linker.Link(confirmedSnapshot));
        long proposedAllocation = MeasureRejectedLinkAllocation(() => linker.Link(proposedSnapshot));
        long maximumPerFailure = oversizedFragment.Length / 8;

        Assert.InRange(confirmedAllocation, 0, maximumPerFailure);
        Assert.InRange(proposedAllocation, 0, maximumPerFailure);
    }

    [Fact]
    public void Candidate_from_another_campaign_is_invalid_and_cannot_shadow_global_content()
    {
        Guid otherCampaign = CovenantTask6Fixture.GuidFor(90);
        CovenantSnapshotCandidate global = CovenantTask6Fixture.GlobalConfirmed(
            "shared.key",
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G2,
            1,
            1);
        CovenantSnapshotCandidate foreign = CovenantTask6Fixture.CampaignConfirmed(
            "shared.key",
            CovenantTask6Fixture.G3,
            CovenantTask6Fixture.G4,
            2,
            2,
            otherCampaign);

        CovenantTurnPlan plan = Link(CovenantTask6Fixture.Snapshot(
            CovenantTask6Fixture.CampaignId,
            foreign,
            global));

        Assert.Equal(CovenantPlanDecision.EligibleConfirmed, DecisionFor(plan, global).Decision);
        Assert.Equal(CovenantPlanDecision.Invalid, DecisionFor(plan, foreign).Decision);
        Assert.Equal([global], plan.GlobalConfirmedSection.Candidates.Select(static decision => decision.Candidate));
    }

    [Fact]
    public void Unsupported_confirmed_policy_fails_closed_while_unsupported_proposed_policy_is_quarantined()
    {
        CovenantSnapshotCandidate unsupportedConfirmed = CovenantTask6Fixture.GlobalConfirmed(
            "unsupported.confirmed",
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G2,
            1,
            1,
            compilerPolicy: 2);
        CovenantSnapshotCandidate unsupportedProposed = CovenantTask6Fixture.CampaignProposed(
            "unsupported.proposed",
            CovenantTask6Fixture.G3,
            CovenantTask6Fixture.G4,
            2,
            2,
            CovenantTask6Fixture.CampaignId,
            compilerPolicy: 2);
        CovenantLinker linker = new();

        Assert.True(linker.Link(CovenantTask6Fixture.Snapshot(null, unsupportedConfirmed)).IsFailure);

        CovenantTurnPlan proposedPlan = linker.Link(CovenantTask6Fixture.Snapshot(
            CovenantTask6Fixture.CampaignId,
            unsupportedProposed)).Value;

        Assert.Equal(CovenantPlanDecision.Quarantined, Assert.Single(proposedPlan.Decisions).Decision);
    }

    [Fact]
    public void Snapshot_and_plan_delegate_the_exact_preimages_to_task_four_literals()
    {
        CovenantTurnPlan plan = CovenantTask6Fixture.IntegrationPlan();
        CovenantTurnSnapshot snapshot = plan.Snapshot;
        SnapshotDigestInput snapshotInput = snapshot.ToDigestInput();
        PlanDigestInput planInput = plan.ToDigestInput();

        Assert.Equal("080291F019A2F8A3EFA0A6AF441F95807B603128543A6D24E1A7D2FAAF3F512E", snapshot.Digest.ToString());
        Assert.Equal("9AD3754B2744287A9E5080EF077FF7DB492B0FBC652CB2C0DC7BB40B5E1EEB27", plan.GlobalConfirmedSection.Digest.ToString());
        Assert.Equal("D3B895D5B8CC981C873D5B60769703897011B10EDD6634298E7D708EFF5D29A8", plan.CampaignConfirmedSection.Digest.ToString());
        Assert.Equal("0DBA0DC79DA6586DC38EF3FE15101055BEF1CED24B1F0F4B322606D23CED5A6F", plan.CampaignProposedSection.Digest.ToString());
        Assert.Equal("2AE4C76097952EF5FC2A28C1C6C01E6AC5047B9C895E509C19BBA83E535637BE", plan.Digest.ToString());
        Assert.Equal(snapshot.Digest, CovenantDigests.Snapshot(snapshotInput));
        Assert.Equal(plan.Digest, CovenantDigests.Plan(planInput));
        Assert.Equal((uint)0, snapshotInput.Candidates[0].ProvenanceCount);
        Assert.Equal(CovenantTask6Fixture.D(3), snapshotInput.Candidates[0].ProvenanceDigest);
        Assert.Equal([CovenantPlacement.GlobalConfirmed, CovenantPlacement.CampaignConfirmed, CovenantPlacement.CampaignProposed], plan.Decisions.Select(static decision => decision.Placement));
        Assert.Equal("```text\n- proposed.a: \"C\"\n```\n", Text(plan.CampaignProposedSection.RenderedBytes));
    }

    [Fact]
    public void Snapshot_and_plan_own_vectors_and_reuse_candidate_payloads_without_string_duplication()
    {
        byte[] fragmentBuffer = "- immutable.key: \"value\"\n"u8.ToArray();
        ImmutableArray<byte> aliasedFragment = ImmutableCollectionsMarshal.AsImmutableArray(fragmentBuffer);
        CovenantSnapshotCandidate candidate = CovenantTask6Fixture.CreateCandidate(
            "immutable.key",
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G2,
            1,
            CovenantScope.Global,
            null,
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            CovenantOrigin.Operator,
            1,
            1,
            CovenantSnapshotCandidateIntegrity.Verified,
            compiledFragment: aliasedFragment);
        CovenantSnapshotCandidate[] candidateBuffer = [candidate];
        ImmutableArray<CovenantSnapshotCandidate> aliasedCandidates = ImmutableCollectionsMarshal.AsImmutableArray(candidateBuffer);
        CovenantTurnSnapshot snapshot = new(
            new CovenantGenerationId(CovenantTask6Fixture.DatasetGeneration),
            null,
            1,
            aliasedCandidates);

        fragmentBuffer.AsSpan().Fill((byte)'x');
        candidateBuffer[0] = CovenantTask6Fixture.GlobalConfirmed(
            "replacement.key",
            CovenantTask6Fixture.G3,
            CovenantTask6Fixture.G4,
            2,
            2);

        CovenantTurnPlan plan = Link(snapshot);
        CovenantPlanCandidateDecision decision = Assert.Single(plan.Decisions);

        Assert.Same(candidate, decision.Candidate);
        Assert.Same(candidate, Assert.Single(plan.GlobalConfirmedSection.Candidates).Candidate);
        Assert.Same(candidate.NormalizedKey.Value, decision.Candidate.NormalizedKey.Value);
        Assert.Equal("- immutable.key: \"value\"\n", Text(candidate.CompiledFragment));
        Assert.Equal("- immutable.key: \"value\"\n", Text(plan.GlobalConfirmedSection.RenderedBytes));
        Assert.Same(plan.ToDigestInput(), plan.ToDigestInput());

        _ = plan.ToDigestInput();
        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int index = 0; index < 1_000; index++)
        {
            _ = plan.ToDigestInput();
            _ = plan.GlobalConfirmedSection.RenderedBytes.Length;
            _ = plan.Decisions[0].Candidate;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.InRange(allocated, 0, 128);
    }

    [Fact]
    public void Snapshot_boundaries_reject_default_invalid_and_ambiguous_shapes()
    {
        CovenantSnapshotCandidate candidate = CovenantTask6Fixture.GlobalConfirmed(
            "valid.key",
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G2,
            1,
            1);

        Assert.Throws<ArgumentException>(() => new CovenantTurnSnapshot(default, null, 1, [candidate]));
        Assert.Throws<ArgumentException>(() => new CovenantTurnSnapshot(new CovenantGenerationId(CovenantTask6Fixture.DatasetGeneration), null, 1, default));
        Assert.Throws<ArgumentException>(() => CovenantTask6Fixture.Snapshot(
            null,
            Enumerable.Repeat(candidate, CovenantLimits.MaxActiveSnapshotRows + 1).ToArray()));
        Assert.Throws<ArgumentException>(() => CovenantTask6Fixture.CreateCandidate(
            "global.proposed",
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G2,
            1,
            CovenantScope.Global,
            null,
            CovenantLane.Proposed,
            CovenantOperation.Set,
            CovenantOrigin.AgentProposed,
            1,
            1,
            CovenantSnapshotCandidateIntegrity.Verified));
        Assert.Throws<ArgumentException>(() => CovenantTask6Fixture.CreateCandidate(
            "missing.campaign",
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G2,
            1,
            CovenantScope.Campaign,
            null,
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            CovenantOrigin.Operator,
            1,
            1,
            CovenantSnapshotCandidateIntegrity.Verified));
        Assert.Throws<ArgumentException>(() => CovenantTask6Fixture.CreateCandidate(
            "too.many.sources",
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G2,
            1,
            CovenantScope.Global,
            null,
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            CovenantOrigin.Operator,
            1,
            65,
            CovenantSnapshotCandidateIntegrity.Verified));
    }

    private static CovenantTurnPlan Link(CovenantTurnSnapshot snapshot)
    {
        var result = new CovenantLinker().Link(snapshot);

        Assert.True(result.IsSuccess, result.Error.Message);

        return result.Value;
    }

    private static CovenantPlanCandidateDecision DecisionFor(
        CovenantTurnPlan plan,
        CovenantSnapshotCandidate candidate) =>
        Assert.Single(plan.Decisions, decision => decision.Candidate == candidate);

    private static string[] Keys(IEnumerable<CovenantPlanCandidateDecision> decisions) =>
        decisions.Select(static decision => decision.Candidate.NormalizedKey.Value).ToArray();

    private static string Text(ImmutableArray<byte> value) =>
        Encoding.UTF8.GetString(value.AsSpan());

    private static void Shuffle<T>(T[] values, Random random)
    {
        for (int index = values.Length - 1; index > 0; index--)
        {
            int selected = random.Next(index + 1);

            (values[index], values[selected]) = (values[selected], values[index]);
        }
    }

    private static ImmutableArray<byte> OversizedFragment()
    {
        byte[] bytes = new byte[256 * 1_024];

        bytes.AsSpan().Fill((byte)'x');
        bytes[^1] = (byte)'\n';

        return ImmutableCollectionsMarshal.AsImmutableArray(bytes);
    }

    private static long MeasureRejectedLinkAllocation(Action action)
    {
        const int Warmups = 8;
        const int Samples = 32;

        for (int index = 0; index < Warmups; index++)
        {
            RequireArgumentFailure(action);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int index = 0; index < Samples; index++)
        {
            RequireArgumentFailure(action);
        }

        return (GC.GetAllocatedBytesForCurrentThread() - before) / Samples;
    }

    private static void RequireArgumentFailure(Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }

        throw new InvalidOperationException("Expected the oversized Section to fail before rendering.");
    }
}

internal static class CovenantTask6Fixture
{
    public static readonly Guid G1 = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static readonly Guid G2 = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public static readonly Guid G3 = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public static readonly Guid G4 = Guid.Parse("44444444-4444-4444-4444-444444444444");

    public static readonly Guid G5 = Guid.Parse("55555555-5555-5555-5555-555555555555");

    public static readonly Guid G6 = Guid.Parse("66666666-6666-6666-6666-666666666666");

    public static readonly Guid DatasetGeneration = Guid.Parse("99999999-9999-9999-9999-999999999999");

    public static readonly Guid CampaignId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    public static readonly Guid BranchId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    public static CovenantSnapshotCandidate GlobalConfirmed(
        string key,
        Guid entryId,
        Guid versionId,
        ulong searchDocumentId,
        byte digestSeed,
        uint compilerPolicy = CovenantCompiler.CompilerPolicyVersion) =>
        CreateCandidate(
            key,
            entryId,
            versionId,
            searchDocumentId,
            CovenantScope.Global,
            null,
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            CovenantOrigin.Operator,
            compilerPolicy,
            0,
            CovenantSnapshotCandidateIntegrity.Verified,
            digestSeed: digestSeed);

    public static CovenantSnapshotCandidate CampaignConfirmed(
        string key,
        Guid entryId,
        Guid versionId,
        ulong searchDocumentId,
        byte digestSeed,
        Guid campaignId,
        CovenantSnapshotCandidateIntegrity integrity = CovenantSnapshotCandidateIntegrity.Verified) =>
        CreateCandidate(
            key,
            entryId,
            versionId,
            searchDocumentId,
            CovenantScope.Campaign,
            campaignId,
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            CovenantOrigin.Operator,
            CovenantCompiler.CompilerPolicyVersion,
            0,
            integrity,
            digestSeed: digestSeed);

    public static CovenantSnapshotCandidate CampaignProposed(
        string key,
        Guid entryId,
        Guid versionId,
        ulong searchDocumentId,
        byte digestSeed,
        Guid campaignId,
        CovenantSnapshotCandidateIntegrity integrity = CovenantSnapshotCandidateIntegrity.Verified,
        uint compilerPolicy = CovenantCompiler.CompilerPolicyVersion) =>
        CreateCandidate(
            key,
            entryId,
            versionId,
            searchDocumentId,
            CovenantScope.Campaign,
            campaignId,
            CovenantLane.Proposed,
            CovenantOperation.Set,
            CovenantOrigin.AgentProposed,
            compilerPolicy,
            0,
            integrity,
            digestSeed: digestSeed);

    public static CovenantSnapshotCandidate CreateCandidate(
        string key,
        Guid entryId,
        Guid versionId,
        ulong searchDocumentId,
        CovenantScope scope,
        Guid? campaignId,
        CovenantLane lane,
        CovenantOperation operation,
        CovenantOrigin origin,
        uint compilerPolicy,
        uint provenanceCount,
        CovenantSnapshotCandidateIntegrity integrity,
        Guid? predecessorId = null,
        uint rendererPolicy = CovenantCompiler.RendererPolicyVersion,
        byte digestSeed = 1,
        ImmutableArray<byte> compiledFragment = default)
    {
        ImmutableArray<byte> fragment = compiledFragment.IsDefault
            ? [.. Encoding.UTF8.GetBytes($"- {key}: \"{key}\"\n")]
            : compiledFragment;

        return new CovenantSnapshotCandidate(
            searchDocumentId,
            entryId,
            versionId,
            new CovenantKey(key),
            scope,
            campaignId,
            lane,
            operation,
            origin,
            searchDocumentId,
            predecessorId,
            compilerPolicy,
            rendererPolicy,
            D(digestSeed),
            D(checked((byte)(digestSeed + 1))),
            provenanceCount,
            D(checked((byte)(digestSeed + 2))),
            fragment,
            integrity);
    }

    public static CovenantTurnSnapshot Snapshot(
        Guid? campaignId,
        params CovenantSnapshotCandidate[] candidates) =>
        new(
            new CovenantGenerationId(DatasetGeneration),
            campaignId,
            4,
            [.. candidates]);

    public static CovenantTurnPlan IntegrationPlan()
    {
        CovenantSnapshotCandidate global = new(
            1,
            G1,
            G2,
            new CovenantKey("global.a"),
            CovenantScope.Global,
            null,
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            CovenantOrigin.Operator,
            1,
            null,
            1,
            1,
            D(1),
            D(2),
            0,
            D(3),
            [.. "- global.a: \"A\"\n"u8],
            CovenantSnapshotCandidateIntegrity.Verified);
        CovenantSnapshotCandidate campaign = new(
            2,
            G3,
            G4,
            new CovenantKey("campaign.a"),
            CovenantScope.Campaign,
            CampaignId,
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            CovenantOrigin.Operator,
            2,
            G2,
            1,
            1,
            D(4),
            D(5),
            1,
            D(6),
            [.. "- campaign.a: \"B\"\n"u8],
            CovenantSnapshotCandidateIntegrity.Verified);
        CovenantSnapshotCandidate proposed = new(
            3,
            G5,
            G6,
            new CovenantKey("proposed.a"),
            CovenantScope.Campaign,
            CampaignId,
            CovenantLane.Proposed,
            CovenantOperation.Set,
            CovenantOrigin.AgentProposed,
            3,
            null,
            1,
            1,
            D(7),
            D(8),
            2,
            D(9),
            [.. "- proposed.a: \"C\"\n"u8],
            CovenantSnapshotCandidateIntegrity.Verified);
        var result = new CovenantLinker().Link(Snapshot(CampaignId, global, campaign, proposed));

        Assert.True(result.IsSuccess, result.Error.Message);

        return result.Value;
    }

    public static Guid GuidFor(int value)
    {
        string hex = value.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);

        return Guid.Parse($"{hex}-0000-0000-0000-{value:x12}");
    }

    public static CovenantDigest D(byte value) =>
        new(Enumerable.Repeat(value, CovenantLimits.DigestBytes).ToArray());

    public static ProviderCallEnvelope ProviderCall(
        string providerIdentity = "provider-a",
        string modelIdentity = "model-a",
        string tokenizerProfile = "tokenizer-v1",
        ulong contextWindowIdentity = 8_192)
    {
        GenerationProvenance provenance = GenerationProvenance.CreateExact([DatasetGeneration]);
        ProviderCallSensitivity sensitivity = new(
            ContentSensitivity.CovenantDerived,
            provenance,
            CovenantDigests.Sensitivity(new SensitivityDigestInput(
                ContentSensitivity.CovenantDerived,
                provenance.Mode,
                provenance.ExactGenerationIds,
                provenance.BloomBits)));
        FrozenProviderOptions options = FrozenProviderOptions.Create(new ProviderOptionsDigestInput(
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            [],
            ProviderToolChoice.Auto,
            null,
            CovenantTriStateBoolean.Absent,
            ProviderResponseFormat.Text,
            null,
            null,
            null,
            CovenantTriStateBoolean.Absent,
            null,
            null,
            null,
            CovenantReasoningWireDialect.Standard,
            default));

        return new ProviderCallEnvelope(
            providerIdentity,
            modelIdentity,
            CovenantProviderDispatchMode.Buffered,
            tokenizerProfile,
            contextWindowIdentity,
            0,
            sensitivity,
            options,
            [],
            [],
            new ProviderCallMaterializationSnapshot(false, []),
            [],
            [],
            null);
    }
}
