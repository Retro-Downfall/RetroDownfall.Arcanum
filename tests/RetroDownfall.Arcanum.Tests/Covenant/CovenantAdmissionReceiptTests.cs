using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Tests.Covenant;

public sealed class CovenantAdmissionReceiptTests
{
    [Fact]
    public void Receipt_retains_the_plan_and_frozen_call_and_derives_every_provider_attempt_identity()
    {
        CovenantTurnPlan plan = CovenantTask6Fixture.IntegrationPlan();
        ProviderCallEnvelope call = CovenantTask6Fixture.ProviderCall();
        CovenantAdmissionReceipt receipt = Receipt(plan, call, AllAdmitted(plan, 11));

        Assert.Same(plan, receipt.Plan);
        Assert.Same(call, receipt.ProviderCall);
        Assert.Equal("provider-a", receipt.ProviderIdentity);
        Assert.Equal("model-a", receipt.ModelIdentity);
        Assert.Equal("tokenizer-v1", receipt.TokenizerProfile);
        Assert.Equal((ulong)8_192, receipt.ContextWindowIdentity);
        Assert.Equal((ulong)0, receipt.CompressionGeneration);
        Assert.Same(call.Options, receipt.ProviderOptions);
        Assert.Same(call.Materialization, receipt.Materialization);
        Assert.Same(call.Sensitivity, receipt.Sensitivity);
        Assert.Equal(call.Digest, receipt.ProviderCallDigest);
        Assert.Equal(call.Materialization.Digest, receipt.MaterializationDigest);
        Assert.Equal(call.Sensitivity.Digest, receipt.SensitivityDigest);
    }

    [Fact]
    public void Receipt_owns_an_exact_eligible_only_vector_and_excludes_every_plan_only_decision()
    {
        CovenantSnapshotCandidate global = CovenantTask6Fixture.GlobalConfirmed(
            "shared.key",
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G2,
            1,
            1);
        CovenantSnapshotCandidate campaign = CovenantTask6Fixture.CampaignConfirmed(
            "shared.key",
            CovenantTask6Fixture.G3,
            CovenantTask6Fixture.G4,
            2,
            2,
            CovenantTask6Fixture.CampaignId);
        CovenantSnapshotCandidate proposed = CovenantTask6Fixture.CampaignProposed(
            "shared.key",
            CovenantTask6Fixture.G3,
            CovenantTask6Fixture.G6,
            3,
            3,
            CovenantTask6Fixture.CampaignId);
        CovenantTurnPlan plan = Link(CovenantTask6Fixture.Snapshot(
            CovenantTask6Fixture.CampaignId,
            proposed,
            global,
            campaign));
        CovenantAdmissionCandidateDecision expected = Decision(
            Assert.Single(plan.EligibleDecisions),
            CovenantAdmissionDecision.Admitted,
            7);
        CovenantAdmissionCandidateDecision[] buffer = [expected];
        ImmutableArray<CovenantAdmissionCandidateDecision> aliased = ImmutableCollectionsMarshal.AsImmutableArray(buffer);
        CovenantAdmissionReceipt receipt = Receipt(plan, CovenantTask6Fixture.ProviderCall(), aliased);

        buffer[0] = new(CovenantTask6Fixture.G1, CovenantTask6Fixture.G2, CovenantAdmissionDecision.RequiredNoFit, 999);

        Assert.Equal(3, plan.Decisions.Length);
        Assert.Single(receipt.EligibleCandidates);
        Assert.Equal(expected, receipt.EligibleCandidates[0]);
        Assert.Equal(campaign.EntryId, receipt.EligibleCandidates[0].EntryId);
        Assert.DoesNotContain(receipt.EligibleCandidates, value => value.EntryId == global.EntryId);
        Assert.DoesNotContain(receipt.EligibleCandidates, value => value.VersionId == proposed.VersionId);
    }

    [Fact]
    public void Stable_plan_reuse_allows_attempt_specific_provider_facts_and_supplied_pressure_outcomes()
    {
        CovenantTurnPlan plan = CovenantTask6Fixture.IntegrationPlan();
        CovenantDigest originalPlanDigest = plan.Digest;
        ImmutableArray<byte> originalProposedBytes = plan.CampaignProposedSection.RenderedBytes;
        CovenantAdmissionReceipt first = Receipt(
            plan,
            CovenantTask6Fixture.ProviderCall(),
            AllAdmitted(plan, 11));
        ProviderCallEnvelope fallbackCall = CovenantTask6Fixture.ProviderCall(
            "provider-b",
            "model-b",
            "tokenizer-v2",
            4_096);
        ImmutableArray<CovenantAdmissionCandidateDecision> fallbackDecisions =
        [
            Decision(plan.EligibleDecisions[0], CovenantAdmissionDecision.Admitted, 21),
            Decision(plan.EligibleDecisions[1], CovenantAdmissionDecision.Admitted, 22),
            Decision(plan.EligibleDecisions[2], CovenantAdmissionDecision.Pressured, 23)
        ];
        CovenantAdmissionReceipt fallback = Receipt(plan, fallbackCall, fallbackDecisions, 2, 2, first.Digest);

        Assert.Same(plan, first.Plan);
        Assert.Same(plan, fallback.Plan);
        Assert.Equal(originalPlanDigest, plan.Digest);
        Assert.Equal(originalProposedBytes, plan.CampaignProposedSection.RenderedBytes);
        Assert.Equal("provider-a", first.ProviderIdentity);
        Assert.Equal("provider-b", fallback.ProviderIdentity);
        Assert.NotEqual(first.ProviderCallDigest, fallback.ProviderCallDigest);
        Assert.NotEqual(first.Digest, fallback.Digest);
        Assert.Empty(fallback.AdmittedCampaignProposedSection.Candidates);
        Assert.Empty(fallback.AdmittedCampaignProposedSection.RenderedBytes);
    }

    [Fact]
    public void Proposed_admission_validates_the_supplied_longest_prefix_without_selecting_it()
    {
        CovenantSnapshotCandidate confirmed = CovenantTask6Fixture.GlobalConfirmed(
            "confirmed.a",
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G2,
            1,
            1);
        CovenantSnapshotCandidate proposedFirst = CovenantTask6Fixture.CampaignProposed(
            "proposed.a",
            CovenantTask6Fixture.G3,
            CovenantTask6Fixture.G4,
            2,
            2,
            CovenantTask6Fixture.CampaignId);
        CovenantSnapshotCandidate proposedSecond = CovenantTask6Fixture.CampaignProposed(
            "proposed.b",
            CovenantTask6Fixture.G5,
            CovenantTask6Fixture.G6,
            3,
            3,
            CovenantTask6Fixture.CampaignId);
        CovenantTurnPlan plan = Link(CovenantTask6Fixture.Snapshot(
            CovenantTask6Fixture.CampaignId,
            proposedSecond,
            confirmed,
            proposedFirst));
        ImmutableArray<CovenantAdmissionCandidateDecision> valid =
        [
            Decision(plan.EligibleDecisions[0], CovenantAdmissionDecision.Admitted, 10),
            Decision(plan.EligibleDecisions[1], CovenantAdmissionDecision.Admitted, 11),
            Decision(plan.EligibleDecisions[2], CovenantAdmissionDecision.Pressured, 12)
        ];

        CovenantAdmissionReceipt receipt = Receipt(plan, CovenantTask6Fixture.ProviderCall(), valid);

        Assert.Equal(valid.ToArray(), receipt.EligibleCandidates.ToArray());
        Assert.Equal([proposedFirst], receipt.AdmittedCampaignProposedSection.Candidates.Select(static value => value.Candidate));
        Assert.Equal(
            "```text\n- proposed.a: \"proposed.a\"\n```\n",
            Text(receipt.AdmittedCampaignProposedSection.RenderedBytes));

        ImmutableArray<CovenantAdmissionCandidateDecision> nonPrefix =
        [
            valid[0],
            valid[1] with { Decision = CovenantAdmissionDecision.Pressured },
            valid[2] with { Decision = CovenantAdmissionDecision.Admitted }
        ];

        Assert.Throws<ArgumentException>(() => Receipt(plan, CovenantTask6Fixture.ProviderCall(), nonPrefix));
    }

    [Fact]
    public void Confirmed_admission_is_validated_as_all_admitted_or_all_required_no_fit()
    {
        CovenantSnapshotCandidate first = CovenantTask6Fixture.GlobalConfirmed(
            "confirmed.a",
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G2,
            1,
            1);
        CovenantSnapshotCandidate second = CovenantTask6Fixture.CampaignConfirmed(
            "confirmed.b",
            CovenantTask6Fixture.G3,
            CovenantTask6Fixture.G4,
            2,
            2,
            CovenantTask6Fixture.CampaignId);
        CovenantSnapshotCandidate proposed = CovenantTask6Fixture.CampaignProposed(
            "proposed.a",
            CovenantTask6Fixture.G5,
            CovenantTask6Fixture.G6,
            3,
            3,
            CovenantTask6Fixture.CampaignId);
        CovenantTurnPlan plan = Link(CovenantTask6Fixture.Snapshot(
            CovenantTask6Fixture.CampaignId,
            proposed,
            second,
            first));
        ImmutableArray<CovenantAdmissionCandidateDecision> noFit =
        [
            Decision(plan.EligibleDecisions[0], CovenantAdmissionDecision.RequiredNoFit, 20),
            Decision(plan.EligibleDecisions[1], CovenantAdmissionDecision.RequiredNoFit, 21),
            Decision(plan.EligibleDecisions[2], CovenantAdmissionDecision.Pressured, 22)
        ];

        CovenantAdmissionReceipt failed = Receipt(plan, CovenantTask6Fixture.ProviderCall(), noFit);

        Assert.Empty(failed.AdmittedGlobalConfirmedSection.RenderedBytes);
        Assert.Empty(failed.AdmittedCampaignConfirmedSection.RenderedBytes);
        Assert.Empty(failed.AdmittedCampaignProposedSection.RenderedBytes);

        ImmutableArray<CovenantAdmissionCandidateDecision> mixedConfirmed = noFit.SetItem(
            0,
            noFit[0] with { Decision = CovenantAdmissionDecision.Admitted });
        ImmutableArray<CovenantAdmissionCandidateDecision> admittedProposedAfterNoFit = noFit.SetItem(
            2,
            noFit[2] with { Decision = CovenantAdmissionDecision.Admitted });
        ImmutableArray<CovenantAdmissionCandidateDecision> pressuredConfirmed = noFit.SetItem(
            0,
            noFit[0] with { Decision = CovenantAdmissionDecision.Pressured });
        ImmutableArray<CovenantAdmissionCandidateDecision> requiredNoFitProposed = noFit.SetItem(
            2,
            noFit[2] with { Decision = CovenantAdmissionDecision.RequiredNoFit });

        Assert.Throws<ArgumentException>(() => Receipt(plan, CovenantTask6Fixture.ProviderCall(), mixedConfirmed));
        Assert.Throws<ArgumentException>(() => Receipt(plan, CovenantTask6Fixture.ProviderCall(), admittedProposedAfterNoFit));
        Assert.Throws<ArgumentException>(() => Receipt(plan, CovenantTask6Fixture.ProviderCall(), pressuredConfirmed));
        Assert.Throws<ArgumentException>(() => Receipt(plan, CovenantTask6Fixture.ProviderCall(), requiredNoFitProposed));
    }

    [Fact]
    public void Eligible_vector_must_match_plan_count_order_and_identities_exactly()
    {
        CovenantTurnPlan plan = CovenantTask6Fixture.IntegrationPlan();
        ImmutableArray<CovenantAdmissionCandidateDecision> valid = AllAdmitted(plan, 1);

        Assert.Throws<ArgumentException>(() => Receipt(plan, CovenantTask6Fixture.ProviderCall(), valid.RemoveAt(2)));
        Assert.Throws<ArgumentException>(() => Receipt(
            plan,
            CovenantTask6Fixture.ProviderCall(),
            [valid[1], valid[0], valid[2]]));
        Assert.Throws<ArgumentException>(() => Receipt(
            plan,
            CovenantTask6Fixture.ProviderCall(),
            valid.SetItem(0, valid[0] with { EntryId = CovenantTask6Fixture.G6 })));
        Assert.Throws<ArgumentException>(() => Receipt(
            plan,
            CovenantTask6Fixture.ProviderCall(),
            valid.SetItem(0, valid[0] with { VersionId = CovenantTask6Fixture.G6 })));
        Assert.Throws<ArgumentOutOfRangeException>(() => Receipt(
            plan,
            CovenantTask6Fixture.ProviderCall(),
            valid.SetItem(0, valid[0] with { Decision = (CovenantAdmissionDecision)0 })));
    }

    [Fact]
    public void Token_and_rendered_byte_totals_use_checked_exact_counts()
    {
        CovenantSnapshotCandidate first = CovenantTask6Fixture.GlobalConfirmed(
            "confirmed.a",
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G2,
            1,
            1);
        CovenantSnapshotCandidate second = CovenantTask6Fixture.GlobalConfirmed(
            "confirmed.b",
            CovenantTask6Fixture.G3,
            CovenantTask6Fixture.G4,
            2,
            2);
        CovenantTurnPlan plan = Link(CovenantTask6Fixture.Snapshot(null, second, first));
        ImmutableArray<CovenantAdmissionCandidateDecision> valid =
        [
            Decision(plan.EligibleDecisions[0], CovenantAdmissionDecision.Admitted, 10),
            Decision(plan.EligibleDecisions[1], CovenantAdmissionDecision.Admitted, 20)
        ];

        CovenantAdmissionReceipt receipt = Receipt(
            plan,
            CovenantTask6Fixture.ProviderCall(),
            valid,
            availableTokenBudget: 100);

        Assert.Equal((ulong)30, receipt.EstimatedConfirmedTokens);
        Assert.Equal((ulong)0, receipt.EstimatedProposedTokens);
        Assert.Equal((ulong)30, receipt.EstimatedTotalTokens);
        Assert.Equal((uint)plan.GlobalConfirmedSection.RenderedBytes.Length, receipt.AdmittedConfirmedBytes);
        Assert.Equal((uint)0, receipt.AdmittedProposedBytes);
        Assert.Equal((uint)plan.GlobalConfirmedSection.RenderedBytes.Length, receipt.AdmittedTotalBytes);

        ImmutableArray<CovenantAdmissionCandidateDecision> overflowing =
        [
            valid[0] with { EstimatedTokens = ulong.MaxValue },
            valid[1] with { EstimatedTokens = 1 }
        ];

        Assert.Throws<OverflowException>(() => Receipt(
            plan,
            CovenantTask6Fixture.ProviderCall(),
            overflowing,
            availableTokenBudget: ulong.MaxValue));
    }

    [Fact]
    public void Per_entry_attribution_above_the_budget_is_not_a_refusal()
    {
        CovenantSnapshotCandidate first = CovenantTask6Fixture.GlobalConfirmed(
            "confirmed.a",
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G2,
            1,
            1);
        CovenantSnapshotCandidate second = CovenantTask6Fixture.GlobalConfirmed(
            "confirmed.b",
            CovenantTask6Fixture.G3,
            CovenantTask6Fixture.G4,
            2,
            2);
        CovenantTurnPlan plan = Link(CovenantTask6Fixture.Snapshot(null, second, first));
        ImmutableArray<CovenantAdmissionCandidateDecision> admitted =
        [
            Decision(plan.EligibleDecisions[0], CovenantAdmissionDecision.Admitted, 60),
            Decision(plan.EligibleDecisions[1], CovenantAdmissionDecision.Admitted, 60)
        ];

        // The shape a real installation reaches: each entry tokenized alone comes to more than the
        // budget, while the planner's tokenization of the same entries rendered together comes to
        // less, because tokens merge across the boundaries the per-entry measure has to pay for. The
        // gap widens with every entry, so asserting on the sum turned a Covenant that fitted into a
        // thrown exception, and the throw landed inside the turn that carried the operator's reply.
        CovenantAdmissionReceipt receipt = Receipt(
            plan,
            CovenantTask6Fixture.ProviderCall(),
            admitted,
            availableTokenBudget: 100,
            plannedAdmittedTokens: 90);

        Assert.Equal((ulong)120, receipt.EstimatedTotalTokens);
        Assert.Equal((ulong)90, receipt.PlannedAdmittedTokens);
        Assert.Equal((ulong)100, receipt.AvailableTokenBudget);
    }

    [Fact]
    public void A_planned_cost_above_the_budget_of_the_same_attempt_is_refused()
    {
        CovenantSnapshotCandidate only = CovenantTask6Fixture.GlobalConfirmed(
            "confirmed.a",
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G2,
            1,
            1);
        CovenantTurnPlan plan = Link(CovenantTask6Fixture.Snapshot(null, only));
        ImmutableArray<CovenantAdmissionCandidateDecision> admitted =
        [
            Decision(plan.EligibleDecisions[0], CovenantAdmissionDecision.Admitted, 1)
        ];

        // The pairing mistake the assertion exists for: a planned cost and a budget that did not come
        // out of one admission decision. The planner never produces this pair, which is the point —
        // it can only arrive by handing the receipt one attempt's plan and another's budget.
        Assert.Throws<ArgumentException>(() => Receipt(
            plan,
            CovenantTask6Fixture.ProviderCall(),
            admitted,
            availableTokenBudget: 100,
            plannedAdmittedTokens: 101));
    }

    [Fact]
    public void Admission_delegates_the_exact_task_four_preimage_to_an_independent_literal()
    {
        CovenantTurnPlan plan = CovenantTask6Fixture.IntegrationPlan();
        ProviderCallEnvelope call = CovenantTask6Fixture.ProviderCall();
        ImmutableArray<CovenantAdmissionCandidateDecision> decisions = AllAdmitted(plan, 11);
        CovenantAdmissionReceipt receipt = Receipt(plan, call, decisions);
        AdmissionDigestInput input = receipt.ToDigestInput();

        Assert.Equal("EC13A460EFEC0D42F76539D80CA3577F69D43523E0D9D8318A3B5463133B3527", call.Sensitivity.Digest.ToString());
        Assert.Equal("9AB75EEA77391D3A9CF058DB6C6FB5E87BEBCB79A6C44C3F3DB2666E4D43D72A", call.Options.Digest.ToString());
        Assert.Equal("B9DFBAFDEB9623312C7797CE54786D899912F49456FA3D95A28C29D6A910E1B1", call.Materialization.Digest.ToString());
        Assert.Equal("7DC25186BCA378DE94CAB83A746FF4C71C3E53B959784AFA6C837EF22B3ACEB2", call.Digest.ToString());
        Assert.Equal("6F97E5CD831FBFC12340DD42B2639ED3628D4F11480998810BC035035CC89EA1", receipt.Digest.ToString());
        Assert.Equal(receipt.Digest, CovenantDigests.Admission(input));
        Assert.Equal(plan.Digest, input.PlanDigest);
        Assert.Equal(call.Digest, input.ProviderCallDigest);
        Assert.Equal(call.Materialization.Digest, input.MaterializationDigest);
        Assert.Equal(call.Sensitivity.Digest, input.SensitivityDigest);
        Assert.Equal(plan.GlobalConfirmedSection.Digest, input.AdmittedGlobalConfirmedSectionDigest);
        Assert.Equal(plan.CampaignConfirmedSection.Digest, input.AdmittedCampaignConfirmedSectionDigest);
        Assert.Equal(plan.CampaignProposedSection.Digest, input.AdmittedCampaignProposedSectionDigest);
        Assert.Equal([11UL, 12UL, 13UL], input.EligibleCandidates.Select(static candidate => candidate.EstimatedTokens));
        Assert.Same(receipt.ToDigestInput(), receipt.ToDigestInput());
    }

    [Fact]
    public void Receipt_boundaries_reject_defaults_invalid_lineage_and_a_plan_that_outruns_its_budget()
    {
        CovenantTurnPlan plan = CovenantTask6Fixture.IntegrationPlan();
        ProviderCallEnvelope call = CovenantTask6Fixture.ProviderCall();
        ImmutableArray<CovenantAdmissionCandidateDecision> valid = AllAdmitted(plan, 1);

        Assert.Throws<ArgumentNullException>(() => new CovenantAdmissionReceipt(
            null!,
            1,
            CovenantTask6Fixture.BranchId,
            1,
            null,
            call,
            4_096,
            0,
            valid));
        Assert.Throws<ArgumentNullException>(() => new CovenantAdmissionReceipt(
            plan,
            1,
            CovenantTask6Fixture.BranchId,
            1,
            null,
            null!,
            4_096,
            0,
            valid));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CovenantAdmissionReceipt(
            plan,
            0,
            CovenantTask6Fixture.BranchId,
            1,
            null,
            call,
            4_096,
            0,
            valid));
        Assert.Throws<ArgumentException>(() => new CovenantAdmissionReceipt(
            plan,
            1,
            Guid.Empty,
            1,
            null,
            call,
            4_096,
            0,
            valid));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CovenantAdmissionReceipt(
            plan,
            1,
            CovenantTask6Fixture.BranchId,
            0,
            null,
            call,
            4_096,
            0,
            valid));
        Assert.Throws<ArgumentException>(() => new CovenantAdmissionReceipt(
            plan,
            1,
            CovenantTask6Fixture.BranchId,
            1,
            default(CovenantDigest),
            call,
            4_096,
            0,
            valid));
        Assert.Throws<ArgumentException>(() => new CovenantAdmissionReceipt(
            plan,
            1,
            CovenantTask6Fixture.BranchId,
            1,
            null,
            call,
            4_096,
            0,
            default));
        // A per-entry attribution above the budget is no longer a refusal: it is what the sum of
        // separately tokenized fragments legitimately looks like once there are enough of them. The
        // boundary the receipt still holds is the planned cost, which is the number the admission
        // decision was actually made with, so that is the one asserted here.
        Assert.Throws<ArgumentException>(() => Receipt(
            plan,
            call,
            valid,
            availableTokenBudget: 4_096,
            plannedAdmittedTokens: 4_097));

        CovenantTurnPlan emptyPlan = Link(CovenantTask6Fixture.Snapshot(null));
        CovenantAdmissionReceipt empty = Receipt(
            emptyPlan,
            call,
            [],
            availableTokenBudget: 0);

        Assert.Empty(empty.EligibleCandidates);
        Assert.Equal((ulong)0, empty.EstimatedTotalTokens);
        Assert.Equal((uint)0, empty.AdmittedTotalBytes);
    }

    private static CovenantAdmissionReceipt Receipt(
        CovenantTurnPlan plan,
        ProviderCallEnvelope call,
        ImmutableArray<CovenantAdmissionCandidateDecision> decisions,
        ulong globalAttemptOrdinal = 1,
        ulong branchOrdinal = 1,
        CovenantDigest? parentAdmissionDigest = null,
        ulong availableTokenBudget = 4_096,
        ulong plannedAdmittedTokens = 0) =>
        new(
            plan,
            globalAttemptOrdinal,
            CovenantTask6Fixture.BranchId,
            branchOrdinal,
            parentAdmissionDigest,
            call,
            availableTokenBudget,
            plannedAdmittedTokens,
            decisions);

    private static ImmutableArray<CovenantAdmissionCandidateDecision> AllAdmitted(
        CovenantTurnPlan plan,
        ulong firstTokenCount) =>
        [
            .. plan.EligibleDecisions.Select((decision, index) =>
                Decision(decision, CovenantAdmissionDecision.Admitted, checked(firstTokenCount + (ulong)index)))
        ];

    private static CovenantAdmissionCandidateDecision Decision(
        CovenantPlanCandidateDecision planDecision,
        CovenantAdmissionDecision decision,
        ulong estimatedTokens) =>
        new(
            planDecision.Candidate.EntryId,
            planDecision.Candidate.VersionId,
            decision,
            estimatedTokens);

    private static CovenantTurnPlan Link(CovenantTurnSnapshot snapshot)
    {
        var result = new CovenantLinker().Link(snapshot);

        Assert.True(result.IsSuccess, result.Error.Message);

        return result.Value;
    }

    private static string Text(ImmutableArray<byte> value) =>
        Encoding.UTF8.GetString(value.AsSpan());
}
