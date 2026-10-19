using RetroDownfall.Arcanum.Core.Annals;

namespace RetroDownfall.Arcanum.Core.LongRest;

/// <summary>
/// Content-free evidence already bound by the owning store to one exact current version. Exposure
/// and committed success are corroboration only; neither is a measured usefulness result.
/// </summary>
public sealed record SagaRankingEvidence(
    int BenefitCount = 0,
    int HarmCount = 0,
    int NoEffectCount = 0,
    DateTimeOffset? LastBenefitAtUtc = null,
    long RetrievalCount = 0,
    long AdmissionCount = 0,
    long SuccessfulUseCount = 0);

/// <summary>Retained version metadata; retirement and consolidation eligibility remain the store's policy.</summary>
public sealed record SagaRankingSnapshot(
    string? VersionId,
    AnnalOrigin? Origin,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ValidFromUtc,
    DateTimeOffset? ValidToUtc,
    bool Pinned,
    bool Retired,
    bool Consolidated,
    SagaRankingEvidence Evidence);

/// <summary>One explainable evaluation, retaining raw similarity separately from its bounded additive score.</summary>
public sealed record SagaRankingComponents(
    int PolicyVersion,
    float Similarity,
    float Score,
    float UsefulnessModifier,
    float RecencyModifier,
    float OriginModifier,
    bool Dormant,
    bool TemporallyValid,
    string EvidenceClass,
    int BenefitCount,
    int HarmCount,
    int NoEffectCount,
    bool DecayExempt,
    string? VersionId);

/// <summary>A pure read-time policy; dormancy excludes a version without retiring or deleting it.</summary>
public static class SagaRankingPolicy
{
    public const int PolicyVersion = 1;

    public const float UsefulnessPerReport = 0.01f;

    public const float MaximumUsefulnessModifier = 0.04f;

    public const float MaximumRecencyPenalty = 0.05f;

    public const float OperatorOriginModifier = 0.09f;

    public const int RecencyPenaltyDays = 180;

    public const int DormancyDays = 365;

    /// <summary>
    /// Applies raw cosine admission outside this policy, then adds usefulness, recency and origin.
    /// Total modifiers remain in [-0.09, +0.13]. At equal cosine the most harmed operator statement
    /// retains +0.05, strictly above a fully reinforced inference's +0.04; an additive score keeps
    /// that ordering transitive. Pins exempt decay only and never manufacture operator origin.
    /// </summary>
    public static SagaRankingComponents Evaluate(
        float similarity,
        SagaRankingSnapshot snapshot,
        DateTimeOffset asOfUtc,
        bool enabled)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        ArgumentNullException.ThrowIfNull(snapshot.Evidence);

        if (!float.IsFinite(similarity))
        {
            throw new ArgumentOutOfRangeException(nameof(similarity), "A ranking requires a finite cosine similarity.");
        }

        SagaRankingEvidence evidence = snapshot.Evidence;

        bool validEvidence = evidence.BenefitCount >= 0 && evidence.HarmCount >= 0 && evidence.NoEffectCount >= 0
            && evidence.RetrievalCount >= 0 && evidence.AdmissionCount >= 0 && evidence.SuccessfulUseCount >= 0;

        int benefits = Math.Max(0, evidence.BenefitCount);

        int harms = Math.Max(0, evidence.HarmCount);

        int noEffects = Math.Max(0, evidence.NoEffectCount);

        bool versioned = !string.IsNullOrWhiteSpace(snapshot.VersionId);

        bool operatorStated = versioned && snapshot.Origin == AnnalOrigin.OperatorStated;

        bool decayExempt = !versioned || snapshot.Pinned || operatorStated;

        // Opt-out preserves the pre-ranking retrieval surface, including temporal eligibility.
        // A legacy row without a version cannot borrow evidence or a claimed origin from another row.
        if (!enabled || !versioned)
        {
            return new(PolicyVersion, similarity, similarity, 0f, 0f, 0f, false, true,
                enabled ? "unversioned" : "disabled", benefits, harms, noEffects, decayExempt, snapshot.VersionId);
        }

        // Widen before subtraction so extreme or malformed retained counts cannot wrap into credit.
        float usefulness = Math.Clamp((long)benefits - harms, -4, 4) * UsefulnessPerReport;

        if (!validEvidence || evidence.SuccessfulUseCount <= 0)
        {
            usefulness = Math.Min(0f, usefulness);
        }

        string evidenceClass = !validEvidence ? "invalid"
            : benefits > 0 && evidence.SuccessfulUseCount == 0 ? "counterfactual-uncorroborated"
            : benefits > 0 || harms > 0 || noEffects > 0 ? "counterfactual"
            : evidence.RetrievalCount > 0 || evidence.AdmissionCount > 0 || evidence.SuccessfulUseCount > 0 ? "exposure-only"
            : "none";

        DateTimeOffset lastUseful = snapshot.CreatedAtUtc;

        // Only valid measured benefit corroborated by committed exact use can refresh age. Counts,
        // no-effect results, malformed evidence and timestamps beyond this snapshot's cut cannot.
        if (validEvidence && benefits > 0 && evidence.SuccessfulUseCount > 0
            && evidence.LastBenefitAtUtc is { } benefit && benefit > lastUseful && benefit <= asOfUtc)
        {
            lastUseful = benefit;
        }

        double ageDays = Math.Max(0d, (asOfUtc - lastUseful).TotalDays);

        float recency = decayExempt ? 0f
            : -(float)Math.Min(1d, ageDays / RecencyPenaltyDays) * MaximumRecencyPenalty;

        bool dormant = !decayExempt && ageDays >= DormancyDays;

        float origin = operatorStated ? OperatorOriginModifier : 0f;

        // A pin protects a live version from decay, never from its explicit validity interval.
        bool temporallyValid = (snapshot.ValidFromUtc is null || snapshot.ValidFromUtc <= asOfUtc)
            && (snapshot.ValidToUtc is null || snapshot.ValidToUtc > asOfUtc);

        return new(PolicyVersion, similarity, similarity + usefulness + recency + origin, usefulness, recency, origin,
            dormant, temporallyValid, evidenceClass, benefits, harms, noEffects, decayExempt, snapshot.VersionId);
    }
}
