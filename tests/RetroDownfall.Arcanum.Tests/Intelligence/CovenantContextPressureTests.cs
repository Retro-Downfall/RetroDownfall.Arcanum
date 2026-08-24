using Microsoft.Extensions.AI;

using RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// What an operator is told when the context budget pressures out agent proposals.
/// </summary>
/// <remarks>
/// An entry that silently failed to reach the model is the failure mode the Covenant exists to
/// prevent. Admission already declines to carry a proposal it cannot fit; these tests cover the other
/// half — that the decline is counted, attributed to the Proposed lane, and readable.
/// </remarks>
public sealed class CovenantContextPressureTests
{

    [Fact]
    public void Pressure_is_recorded_as_the_attempt_that_dispatched_rather_than_accumulated()
    {

        ContextMaterializationLedger ledger = CreateLedger();

        ledger.RecordCovenantPressure(3, 240);

        // A retry runs under a different budget and reaches a different admission. Accumulating would
        // report six entries pressured when the turn that actually dispatched pressured two.
        ledger.RecordCovenantPressure(2, 160);

        Assert.Equal(2, ledger.DroppedCovenantProposed);

        Assert.Equal(160, ledger.DroppedCovenantProposedTokens);

    }

    [Fact]
    public void A_turn_that_pressured_nothing_records_a_measured_zero()
    {

        ContextMaterializationLedger ledger = CreateLedger();

        ledger.RecordCovenantPressure(0, 0);

        Assert.Equal(0, ledger.DroppedCovenantProposed);

        Assert.False(Breakdown(0, 0).HasCovenantPressure);

    }

    [Fact]
    public void Proposed_pressure_is_never_folded_into_the_semantic_retrieval_totals()
    {

        ContextTokenBreakdown breakdown = Breakdown(4, 320);

        // Confirmed content ranks with the operator's own Codex and is never evicted. A single
        // "dropped memory" total would let a reader conclude their standing agreement had been
        // trimmed when only the agent's unreviewed suggestions were.
        Assert.Equal(0, breakdown.DroppedSemanticRagChunks);

        Assert.Equal(0, breakdown.DroppedSemanticRagTokens);

        Assert.True(breakdown.HasCovenantPressure);

    }

    [Fact]
    public void Context_inspect_names_the_pressured_entries_on_the_proposed_lane()
    {

        ContextPreviewSource proposed = ProposedLane(Breakdown(4, 320));

        Assert.Contains("4 proposed entries", proposed.Reason, StringComparison.Ordinal);

        Assert.Contains("320 tokens", proposed.Reason, StringComparison.Ordinal);

        Assert.Contains("Confirmed content is never evicted", proposed.Reason, StringComparison.Ordinal);

    }

    [Fact]
    public void A_single_pressured_entry_is_reported_in_the_singular()
    {

        Assert.Contains("1 proposed entry ", ProposedLane(Breakdown(1, 80)).Reason, StringComparison.Ordinal);

    }

    [Fact]
    public void An_unpressured_turn_keeps_the_ordinary_lane_reason()
    {

        // The pressure sentence has to be absent, not zeroed: "0 proposed entries were pressured out"
        // reads as a warning to an operator scanning for one.
        string reason = ProposedLane(Breakdown(0, 0)).Reason;

        Assert.DoesNotContain("pressured out", reason, StringComparison.Ordinal);

    }

    [Fact]
    public void Pressure_is_reported_only_on_the_lane_it_happened_to()
    {

        List<ContextPreviewSource> sources = BuildSources(Breakdown(4, 320));

        foreach (ContextPreviewSource source in sources)
        {

            if (source.Source is ContextTokenSource.CovenantProposed)
            {

                continue;

            }

            Assert.DoesNotContain("pressured out", source.Reason, StringComparison.Ordinal);

        }

    }

    [Fact]
    public void Pressure_measures_what_was_dropped_rather_than_what_was_admitted()
    {

        CovenantAdmissionPlan plan = Plan(
            Candidate(CovenantAdmissionDecision.Admitted, 500),
            Candidate(CovenantAdmissionDecision.Pressured, 70),
            Candidate(CovenantAdmissionDecision.Admitted, 400),
            Candidate(CovenantAdmissionDecision.Pressured, 30));

        // The admitted tokens are the ones the model read. Reporting them as pressure would tell an
        // operator investigating a dropped preference that the entries they lost were the ones that
        // arrived — the exact inversion of the answer they came for.
        Assert.Equal(100ul, plan.PressuredProposedTokens);

        Assert.NotEqual(plan.EstimatedAdmittedTokens, plan.PressuredProposedTokens);

    }

    [Fact]
    public void An_entry_that_could_never_fit_is_not_counted_as_pressured()
    {

        // RequiredNoFit is a different failure with a different remedy: pressure means the budget was
        // tight this turn, and no-fit means the entry cannot be carried at any budget.
        CovenantAdmissionPlan plan = Plan(
            Candidate(CovenantAdmissionDecision.Pressured, 40),
            Candidate(CovenantAdmissionDecision.RequiredNoFit, 9_000));

        Assert.Equal(40ul, plan.PressuredProposedTokens);

    }

    private static CovenantAdmissionPlan Plan(params CovenantAdmissionCandidateDecision[] candidates)
    {

        ulong admitted = 0;

        foreach (CovenantAdmissionCandidateDecision candidate in candidates)
        {

            if (candidate.Decision is CovenantAdmissionDecision.Admitted)
            {

                admitted += candidate.EstimatedTokens;

            }

        }

        int removals = candidates.Count(static candidate =>
            candidate.Decision is CovenantAdmissionDecision.Pressured);

        return new CovenantAdmissionPlan(
            [.. candidates],
            CovenantPromptContent.None,
            ConfirmedAdmitted: true,
            AdmittedProposedCount: candidates.Length - removals,
            ProposedRemovals: removals,
            EstimatedAdmittedTokens: admitted);

    }

    private static CovenantAdmissionCandidateDecision Candidate(CovenantAdmissionDecision decision, ulong tokens) =>
        new(Guid.NewGuid(), Guid.NewGuid(), decision, tokens);

    private static ContextPreviewSource ProposedLane(ContextTokenBreakdown breakdown) =>
        BuildSources(breakdown).Single(static source => source.Source is ContextTokenSource.CovenantProposed);

    private static List<ContextPreviewSource> BuildSources(ContextTokenBreakdown breakdown) =>
        WizardIntelligenceProvider.BuildPreviewSources(
            breakdown,
            new ContextPreviewRequest(Prompt: "why was my preference not honored?"),
            new PingRequest("why was my preference not honored?"),
            []);

    private static ContextTokenBreakdown Breakdown(int droppedEntries, int droppedTokens) =>
        new()
        {
            Provider = "provider",
            Model = "model",
            Profile = new ResolvedModelTokenizationProfile
            {
                ProfileId = "test",
                Type = ModelTokenizationProfileType.UnknownFallback,
                TokenizerId = "o200k_base",
                SafetyMarginPercent = 15,
                PerMessageOverheadTokens = 4,
                PerToolOverheadTokens = 8,
                ProviderFramingTokens = 3,
                StopTokenOverheadTokens = 1,
                UnknownImageReserveTokens = 2048,
                Confidence = 0.5,
            },
            Components =
            [
                Component(ContextTokenSource.CovenantConfirmed, 210),
                Component(ContextTokenSource.CovenantProposed, 60),
                Component(ContextTokenSource.AttachmentRag, 40),
            ],
            InputTokens = 310,
            ReservedTokens = 1_024,
            TotalTokens = 1_334,
            OverallClassification = TokenEstimateClassification.Estimated,
            SafetyMarginTokens = 0,
            DroppedCovenantProposed = droppedEntries,
            DroppedCovenantProposedTokens = droppedTokens,
        };

    private static ContextTokenComponent Component(ContextTokenSource source, int tokens) =>
        new(source, new TokenEstimate(tokens, TokenEstimateClassification.Estimated, "test"));

    private static ContextMaterializationLedger CreateLedger() =>
        new(
            Guid.NewGuid(),
            new ContextMaterializationLimits(
                MaxRetrievedChunks: 20,
                MaxRetrievedAttachments: 8,
                MaxRetrievedBytes: 1024,
                MaxRetrievedTokens: 256));

}
