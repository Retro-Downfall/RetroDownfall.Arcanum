using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// One provider attempt's admission outcome, before the provider call is frozen.
/// </summary>
public sealed record CovenantAdmissionPlan(
    ImmutableArray<CovenantAdmissionCandidateDecision> Candidates,
    CovenantPromptContent AdmittedContent,
    bool ConfirmedAdmitted,
    int AdmittedProposedCount,
    int ProposedRemovals,
    ulong EstimatedAdmittedTokens)
{

    /// <summary>
    /// Estimated tokens the Proposed entries this attempt pressured out would have occupied.
    /// </summary>
    /// <remarks>
    /// Summed over the pressured candidates alone. <see cref="EstimatedAdmittedTokens"/> is the cost
    /// of what reached the model, and reporting it as pressure would tell an operator investigating a
    /// dropped preference that the entries they lost were the ones the model actually read.
    ///
    /// <para>Composed but never non-zero on an installation. This runs on every turn the Covenant
    /// feature is on, and it runs over an empty Proposed set: the lane's only producer is the agent
    /// mutation factory, whose staged intents are always discarded, and the operator surface writes
    /// the Confirmed lane only. So no Proposed head can exist to be pressured out, and only tests
    /// reach a non-zero total. The arithmetic is proven, the surface it reports on is not.</para>
    /// </remarks>
    public ulong PressuredProposedTokens
    {

        get
        {

            ulong total = 0;

            foreach (CovenantAdmissionCandidateDecision candidate in Candidates)
            {

                if (candidate.Decision is CovenantAdmissionDecision.Pressured)
                {

                    total += candidate.EstimatedTokens;

                }

            }

            return total;

        }

    }

}

/// <summary>
/// Decides what one provider attempt admits from a reused turn plan (§10.13).
/// </summary>
/// <remarks>
/// Pure and provider-independent by construction: everything model-specific arrives through the two
/// measurement delegates. That is what lets two fallback candidates with different tokenizers and
/// different remaining budgets reach different Proposed admissions from the same bytes and the same
/// revision vector, while Confirmed stays all-or-fail for both.
/// </remarks>
public static class CovenantAdmissionPlanner
{

    /// <summary>The bounded number of Proposed removals one attempt may perform.</summary>
    public const int MaxProposedRemovals = 32;

    /// <summary>
    /// Admits Confirmed all-or-fail, then the longest Proposed prefix whose complete framed section
    /// fits the remaining budget.
    /// </summary>
    /// <param name="plan">The reused, provider-independent turn plan.</param>
    /// <param name="availableTokenBudget">Tokens this attempt may spend on Covenant content.</param>
    /// <param name="measureSections">
    /// Measures the complete framed cost of one candidate rendering, including headings, the
    /// untrusted-data notice, fences, and separators.
    /// </param>
    /// <param name="measureFragment">Measures one compiled fragment alone, for per-candidate evidence.</param>
    public static CovenantAdmissionPlan Plan(
        CovenantTurnPlan plan,
        ulong availableTokenBudget,
        Func<CovenantPromptContent, ulong> measureSections,
        Func<string, ulong> measureFragment)
    {

        ArgumentNullException.ThrowIfNull(plan);

        ArgumentNullException.ThrowIfNull(measureSections);

        ArgumentNullException.ThrowIfNull(measureFragment);

        int proposedTotal = plan.CampaignProposedSection.Candidates.Length;

        CovenantPromptContent confirmedOnly = CovenantPromptContent.FromAdmittedPrefix(plan, true, 0);

        if (measureSections(confirmedOnly) > availableTokenBudget)
        {
            return Refused(plan, measureFragment);
        }

        int admitted = proposedTotal;

        int removals = 0;

        CovenantPromptContent content = CovenantPromptContent.FromAdmittedPrefix(plan, true, admitted);

        ulong cost = measureSections(content);

        // Remove the suffix in reverse plan order and retokenize the exact remaining section after
        // each removal. Estimating the delta instead would let a byte-pair boundary shift make the
        // recorded cost disagree with the bytes the receipt binds.
        while (cost > availableTokenBudget && admitted > 0 && removals < MaxProposedRemovals)
        {

            admitted--;

            removals++;

            content = CovenantPromptContent.FromAdmittedPrefix(plan, true, admitted);

            cost = measureSections(content);

        }

        if (cost > availableTokenBudget)
        {
            return Refused(plan, measureFragment);
        }

        return new CovenantAdmissionPlan(
            Decide(plan, measureFragment, confirmedAdmitted: true, admitted),
            content,
            ConfirmedAdmitted: true,
            admitted,
            removals,
            cost);

    }

    private static CovenantAdmissionPlan Refused(
        CovenantTurnPlan plan,
        Func<string, ulong> measureFragment) =>
        new(
            Decide(plan, measureFragment, confirmedAdmitted: false, admittedProposedCount: 0),
            CovenantPromptContent.None,
            ConfirmedAdmitted: false,
            AdmittedProposedCount: 0,
            ProposedRemovals: plan.CampaignProposedSection.Candidates.Length,
            EstimatedAdmittedTokens: 0);

    private static ImmutableArray<CovenantAdmissionCandidateDecision> Decide(
        CovenantTurnPlan plan,
        Func<string, ulong> measureFragment,
        bool confirmedAdmitted,
        int admittedProposedCount)
    {

        ImmutableArray<CovenantAdmissionCandidateDecision>.Builder builder =
            ImmutableArray.CreateBuilder<CovenantAdmissionCandidateDecision>(plan.EligibleDecisions.Length);

        int proposedSeen = 0;

        foreach (CovenantPlanCandidateDecision decision in plan.EligibleDecisions)
        {

            bool confirmed = decision.Decision is CovenantPlanDecision.EligibleConfirmed;

            CovenantAdmissionDecision outcome = confirmed
                ? confirmedAdmitted
                    ? CovenantAdmissionDecision.Admitted
                    : CovenantAdmissionDecision.RequiredNoFit
                : proposedSeen++ < admittedProposedCount && confirmedAdmitted
                    ? CovenantAdmissionDecision.Admitted
                    : CovenantAdmissionDecision.Pressured;

            builder.Add(new CovenantAdmissionCandidateDecision(
                decision.Candidate.EntryId,
                decision.Candidate.VersionId,
                outcome,
                measureFragment(FragmentText(decision))));

        }

        return builder.ToImmutable();

    }

    private static string FragmentText(CovenantPlanCandidateDecision decision) =>
        System.Text.Encoding.UTF8.GetString(decision.Candidate.CompiledFragment.AsSpan());

}
