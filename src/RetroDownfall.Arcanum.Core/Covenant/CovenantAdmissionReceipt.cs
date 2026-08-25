using System.Collections.Immutable;

namespace RetroDownfall.Arcanum.Core.Covenant;

public readonly record struct CovenantAdmissionCandidateDecision(
    Guid EntryId,
    Guid VersionId,
    CovenantAdmissionDecision Decision,
    ulong EstimatedTokens);

public sealed class CovenantAdmissionReceipt
{
    private readonly AdmissionDigestInput _digestInput;

    public CovenantAdmissionReceipt(
        CovenantTurnPlan plan,
        ulong globalAttemptOrdinal,
        Guid branchId,
        ulong branchOrdinal,
        CovenantDigest? parentAdmissionDigest,
        ProviderCallEnvelope providerCall,
        ulong availableTokenBudget,
        ulong plannedAdmittedTokens,
        ImmutableArray<CovenantAdmissionCandidateDecision> eligibleCandidates)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        GlobalAttemptOrdinal = CovenantValidation.RequirePositive(
            globalAttemptOrdinal,
            nameof(globalAttemptOrdinal));
        BranchId = CovenantValidation.RequireNonEmpty(branchId, nameof(branchId));
        BranchOrdinal = CovenantValidation.RequirePositive(branchOrdinal, nameof(branchOrdinal));
        ParentAdmissionDigest = ValidateOptionalDigest(
            parentAdmissionDigest,
            nameof(parentAdmissionDigest));
        ProviderCall = providerCall ?? throw new ArgumentNullException(nameof(providerCall));
        AvailableTokenBudget = availableTokenBudget;
        PlannedAdmittedTokens = plannedAdmittedTokens;
        EligibleCandidates = FreezeAndValidateCandidates(plan, eligibleCandidates);

        ValidateClosedDecisionShape(plan, EligibleCandidates);

        AdmittedGlobalConfirmedSection = BuildAdmittedSection(
            plan.GlobalConfirmedSection,
            plan,
            EligibleCandidates);
        AdmittedCampaignConfirmedSection = BuildAdmittedSection(
            plan.CampaignConfirmedSection,
            plan,
            EligibleCandidates);
        AdmittedCampaignProposedSection = BuildAdmittedSection(
            plan.CampaignProposedSection,
            plan,
            EligibleCandidates);

        (EstimatedConfirmedTokens, EstimatedProposedTokens) =
            CalculateTokenCounts(plan, EligibleCandidates);
        EstimatedTotalTokens = checked(EstimatedConfirmedTokens + EstimatedProposedTokens);

        // Judged against the planner's own measurement of the admitted sections, not against a second
        // one computed here. The per-candidate estimates above are attribution: they tokenize each
        // fragment alone, and a fragment tokenized alone cannot merge across the boundary it shares
        // with its neighbour, so their sum runs ahead of the joint measure by a margin that grows with
        // every entry. Asserting on that sum refused a Covenant the planner had correctly fitted, and
        // it did so by throwing out of a turn — which cost the operator the answer they had paid for
        // on every turn once the installation held enough entries. What this still catches is the
        // pairing mistake it was written for: a budget and a plan that did not come from one decision.
        if (PlannedAdmittedTokens > AvailableTokenBudget)
        {
            throw new ArgumentException("The planned admitted token cost exceeds the budget the same attempt was given.", nameof(plannedAdmittedTokens));
        }

        AdmittedConfirmedBytes = checked((uint)(
            AdmittedGlobalConfirmedSection.RenderedBytes.Length
            + AdmittedCampaignConfirmedSection.RenderedBytes.Length));
        AdmittedProposedBytes = checked((uint)AdmittedCampaignProposedSection.RenderedBytes.Length);
        AdmittedTotalBytes = checked(AdmittedConfirmedBytes + AdmittedProposedBytes);
        _digestInput = new AdmissionDigestInput(
            Plan.Digest,
            GlobalAttemptOrdinal,
            BranchId,
            BranchOrdinal,
            ParentAdmissionDigest,
            ProviderCall.Digest,
            ProviderCall.Materialization.Digest,
            ProviderCall.Sensitivity.Digest,
            AvailableTokenBudget,
            [.. EligibleCandidates.Select(static candidate => new AdmissionCandidateDigestInput(
                candidate.EntryId,
                candidate.VersionId,
                candidate.Decision,
                candidate.EstimatedTokens))],
            AdmittedGlobalConfirmedSection.Digest,
            AdmittedCampaignConfirmedSection.Digest,
            AdmittedCampaignProposedSection.Digest);
        Digest = CovenantDigests.Admission(_digestInput);
    }

    public CovenantTurnPlan Plan { get; }

    public ulong GlobalAttemptOrdinal { get; }

    public Guid BranchId { get; }

    public ulong BranchOrdinal { get; }

    public CovenantDigest? ParentAdmissionDigest { get; }

    public ProviderCallEnvelope ProviderCall { get; }

    public string ProviderIdentity => ProviderCall.ProviderIdentity;

    public string ModelIdentity => ProviderCall.ModelIdentity;

    public string TokenizerProfile => ProviderCall.TokenizerProfile;

    public ulong ContextWindowIdentity => ProviderCall.ContextWindowIdentity;

    public ulong CompressionGeneration => ProviderCall.CompressionGeneration;

    public FrozenProviderOptions ProviderOptions => ProviderCall.Options;

    public ProviderCallMaterializationSnapshot Materialization => ProviderCall.Materialization;

    public ProviderCallSensitivity Sensitivity => ProviderCall.Sensitivity;

    public CovenantDigest ProviderCallDigest => ProviderCall.Digest;

    public CovenantDigest MaterializationDigest => ProviderCall.Materialization.Digest;

    public CovenantDigest SensitivityDigest => ProviderCall.Sensitivity.Digest;

    public ulong AvailableTokenBudget { get; }

    /// <summary>
    /// What the admission planner measured the sections it admitted to cost.
    /// </summary>
    /// <remarks>
    /// The tokenization of the rendered admitted content as one text, which is the number the fit
    /// decision was made with and therefore the only one that can be compared to the budget without
    /// the two disagreeing. <see cref="EstimatedTotalTokens"/> is the per-entry attribution and is
    /// expected to run above this; it answers which entry cost what, not whether the whole fitted.
    /// </remarks>
    public ulong PlannedAdmittedTokens { get; }

    public ImmutableArray<CovenantAdmissionCandidateDecision> EligibleCandidates { get; }

    public CovenantTurnSection AdmittedGlobalConfirmedSection { get; }

    public CovenantTurnSection AdmittedCampaignConfirmedSection { get; }

    public CovenantTurnSection AdmittedCampaignProposedSection { get; }

    public ulong EstimatedConfirmedTokens { get; }

    public ulong EstimatedProposedTokens { get; }

    public ulong EstimatedTotalTokens { get; }

    public uint AdmittedConfirmedBytes { get; }

    public uint AdmittedProposedBytes { get; }

    public uint AdmittedTotalBytes { get; }

    public CovenantDigest Digest { get; }

    public AdmissionDigestInput ToDigestInput() =>
        _digestInput;

    private static CovenantDigest? ValidateOptionalDigest(
        CovenantDigest? value,
        string parameterName) =>
        value is { } present
            ? CovenantValidation.RequireDigest(present, parameterName)
            : null;

    private static ImmutableArray<CovenantAdmissionCandidateDecision> FreezeAndValidateCandidates(
        CovenantTurnPlan plan,
        ImmutableArray<CovenantAdmissionCandidateDecision> candidates)
    {
        if (candidates.IsDefault)
        {
            throw new ArgumentException("The eligible admission vector must be initialized.", nameof(candidates));
        }

        CovenantAdmissionCandidateDecision[] frozen = candidates.ToArray();

        if (frozen.Length != plan.EligibleDecisions.Length)
        {
            throw new ArgumentException("The admission vector must match the Plan eligible count exactly.", nameof(candidates));
        }

        for (int index = 0; index < frozen.Length; index++)
        {
            CovenantAdmissionCandidateDecision candidate = frozen[index];
            CovenantPlanCandidateDecision planned = plan.EligibleDecisions[index];

            ValidateAdmissionDecision(candidate.Decision);

            if (candidate.EntryId != planned.Candidate.EntryId
                || candidate.VersionId != planned.Candidate.VersionId)
            {
                throw new ArgumentException("The admission vector must preserve exact Plan eligible order and identities.", nameof(candidates));
            }
        }

        return [.. frozen];
    }

    /// <summary>
    /// Checks the closed decision shape one attempt is allowed to have.
    /// </summary>
    /// <remarks>
    /// The Confirmed arm is exercised on every turn. The Proposed arms below it — the Admitted-or-
    /// Pressured alphabet, the admitted-prefix-then-pressured-suffix order, and the no-fit rule that
    /// forbids admitting Proposed content when Confirmed did not fit — are reached once an agent
    /// proposal has been published, which happens when the turn that staged it commits its answer.
    /// The no-fit rule is the one that has to hold: a turn too tight to seat the operator's own
    /// Confirmed instruction must not seat the agent's suggestions in its place.
    /// </remarks>
    private static void ValidateClosedDecisionShape(
        CovenantTurnPlan plan,
        ImmutableArray<CovenantAdmissionCandidateDecision> candidates)
    {
        CovenantAdmissionDecision? confirmedDecision = null;
        bool proposedPressureStarted = false;

        for (int index = 0; index < candidates.Length; index++)
        {
            CovenantPlanCandidateDecision planned = plan.EligibleDecisions[index];
            CovenantAdmissionDecision decision = candidates[index].Decision;

            if (planned.Decision == CovenantPlanDecision.EligibleConfirmed)
            {
                if (decision is not CovenantAdmissionDecision.Admitted
                    and not CovenantAdmissionDecision.RequiredNoFit)
                {
                    throw new ArgumentException("Confirmed admission accepts only Admitted or RequiredNoFit.", nameof(candidates));
                }

                confirmedDecision ??= decision;

                if (confirmedDecision != decision)
                {
                    throw new ArgumentException("Global and Campaign Confirmed admission is one all-or-fail decision.", nameof(candidates));
                }

                continue;
            }

            if (decision is not CovenantAdmissionDecision.Admitted
                and not CovenantAdmissionDecision.Pressured)
            {
                throw new ArgumentException("Proposed admission accepts only Admitted or Pressured.", nameof(candidates));
            }

            if (decision == CovenantAdmissionDecision.Pressured)
            {
                proposedPressureStarted = true;
            }
            else if (proposedPressureStarted)
            {
                throw new ArgumentException("Proposed admission must preserve one admitted prefix and pressured suffix.", nameof(candidates));
            }
        }

        if (confirmedDecision == CovenantAdmissionDecision.RequiredNoFit
            && candidates.Where((_, index) =>
                plan.EligibleDecisions[index].Decision == CovenantPlanDecision.EligibleProposed)
                .Any(static candidate => candidate.Decision != CovenantAdmissionDecision.Pressured))
        {
            throw new ArgumentException("A Confirmed no-fit attempt cannot admit Proposed content.", nameof(candidates));
        }
    }

    private static CovenantTurnSection BuildAdmittedSection(
        CovenantTurnSection plannedSection,
        CovenantTurnPlan plan,
        ImmutableArray<CovenantAdmissionCandidateDecision> candidates)
    {
        List<CovenantPlanCandidateDecision> admitted = [];

        for (int index = 0; index < candidates.Length; index++)
        {
            CovenantPlanCandidateDecision planned = plan.EligibleDecisions[index];

            if (planned.Placement == plannedSection.Placement
                && candidates[index].Decision == CovenantAdmissionDecision.Admitted)
            {
                admitted.Add(planned);
            }
        }

        if (admitted.Count == plannedSection.Candidates.Length
            && admitted.SequenceEqual(plannedSection.Candidates))
        {
            return plannedSection;
        }

        return CovenantTurnSection.Create(plannedSection.Placement, admitted);
    }

    private static (ulong Confirmed, ulong Proposed) CalculateTokenCounts(
        CovenantTurnPlan plan,
        ImmutableArray<CovenantAdmissionCandidateDecision> candidates)
    {
        ulong confirmed = 0;
        ulong proposed = 0;

        for (int index = 0; index < candidates.Length; index++)
        {
            CovenantAdmissionCandidateDecision candidate = candidates[index];
            CovenantPlanCandidateDecision planned = plan.EligibleDecisions[index];

            if (planned.Decision == CovenantPlanDecision.EligibleConfirmed)
            {
                confirmed = checked(confirmed + candidate.EstimatedTokens);
            }
            else
            {
                proposed = checked(proposed + candidate.EstimatedTokens);
            }

        }

        return (confirmed, proposed);
    }

    private static void ValidateAdmissionDecision(CovenantAdmissionDecision decision)
    {
        if (decision is not CovenantAdmissionDecision.Admitted
            and not CovenantAdmissionDecision.Pressured
            and not CovenantAdmissionDecision.RequiredNoFit)
        {
            throw new ArgumentOutOfRangeException(nameof(decision));
        }
    }
}
