using RetroDownfall.Arcanum.Core.Primitives;
using System.Text;

namespace RetroDownfall.Arcanum.Core.Covenant;

public sealed class CovenantLinker : ICovenantLinker
{
    public Result<CovenantTurnPlan> Link(CovenantTurnSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Section rendering enforces the per-placement byte ceilings by throwing, and nothing on the
        // write path stops an installation from accumulating entries that together exceed one. That
        // is a fact about stored state, not a programming error, so it degrades the turn through the
        // ordinary failure channel the dispatch gate already handles rather than escaping as an
        // exception past the gate's never-fail-the-turn contract and taking the turn lease with it.
        try
        {
            return LinkCore(snapshot);
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
        {
            return Result<CovenantTurnPlan>.Failure(new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "The stored Covenant cannot be rendered within its placement bounds."));
        }
    }

    private static Result<CovenantTurnPlan> LinkCore(CovenantTurnSnapshot snapshot)
    {
        CovenantSnapshotCandidate[] candidates = snapshot.Candidates.ToArray();

        Array.Sort(candidates, CandidateComparer.Instance);

        Result integrity = ValidateAuthoritativeCandidates(snapshot, candidates);

        if (integrity.IsFailure)
        {
            return Result<CovenantTurnPlan>.Failure(integrity.Error);
        }

        Dictionary<string, CovenantSnapshotCandidate> campaignConfirmedByKey =
            BuildCampaignConfirmedIndex(snapshot, candidates);
        HashSet<string> effectiveConfirmedKeys = BuildEffectiveConfirmedKeys(
            snapshot,
            candidates,
            campaignConfirmedByKey);
        CovenantPlanCandidateDecision[] decisions = new CovenantPlanCandidateDecision[candidates.Length];

        for (int index = 0; index < candidates.Length; index++)
        {
            decisions[index] = Decide(
                snapshot,
                candidates[index],
                campaignConfirmedByKey,
                effectiveConfirmedKeys);
        }

        CovenantTurnSection globalConfirmed = CovenantTurnSection.Create(
            CovenantPlacement.GlobalConfirmed,
            decisions.Where(static decision =>
                decision.Placement == CovenantPlacement.GlobalConfirmed
                && decision.Decision == CovenantPlanDecision.EligibleConfirmed));
        CovenantTurnSection campaignConfirmed = CovenantTurnSection.Create(
            CovenantPlacement.CampaignConfirmed,
            decisions.Where(static decision =>
                decision.Placement == CovenantPlacement.CampaignConfirmed
                && decision.Decision == CovenantPlanDecision.EligibleConfirmed));
        CovenantTurnSection campaignProposed = CovenantTurnSection.Create(
            CovenantPlacement.CampaignProposed,
            decisions.Where(static decision =>
                decision.Placement == CovenantPlacement.CampaignProposed
                && decision.Decision == CovenantPlanDecision.EligibleProposed));

        return Result<CovenantTurnPlan>.Success(new CovenantTurnPlan(
            snapshot,
            decisions,
            globalConfirmed,
            campaignConfirmed,
            campaignProposed));
    }

    private static Result ValidateAuthoritativeCandidates(
        CovenantTurnSnapshot snapshot,
        ReadOnlySpan<CovenantSnapshotCandidate> candidates)
    {
        foreach (CovenantSnapshotCandidate candidate in candidates)
        {
            if (candidate.Lane != CovenantLane.Confirmed || !IsInScope(snapshot, candidate))
            {
                continue;
            }

            if (candidate.Integrity != CovenantSnapshotCandidateIntegrity.Verified)
            {
                return IntegrityFailure("An active Confirmed Covenant candidate failed integrity validation.");
            }

            if (!HasSupportedPolicies(candidate))
            {
                return IntegrityFailure("An active Confirmed Covenant candidate uses an unsupported policy version.");
            }
        }

        return Result.Success();
    }

    private static Dictionary<string, CovenantSnapshotCandidate> BuildCampaignConfirmedIndex(
        CovenantTurnSnapshot snapshot,
        ReadOnlySpan<CovenantSnapshotCandidate> candidates)
    {
        Dictionary<string, CovenantSnapshotCandidate> result = new(StringComparer.Ordinal);

        foreach (CovenantSnapshotCandidate candidate in candidates)
        {
            if (candidate.Scope != CovenantScope.Campaign
                || candidate.Lane != CovenantLane.Confirmed
                || !IsInScope(snapshot, candidate)
                || candidate.Integrity != CovenantSnapshotCandidateIntegrity.Verified
                || !HasSupportedPolicies(candidate))
            {
                continue;
            }

            result.TryAdd(candidate.NormalizedKey.Value, candidate);
        }

        return result;
    }

    private static HashSet<string> BuildEffectiveConfirmedKeys(
        CovenantTurnSnapshot snapshot,
        ReadOnlySpan<CovenantSnapshotCandidate> candidates,
        IReadOnlyDictionary<string, CovenantSnapshotCandidate> campaignConfirmedByKey)
    {
        HashSet<string> result = new(StringComparer.Ordinal);

        foreach (CovenantSnapshotCandidate candidate in candidates)
        {
            if (candidate.Lane != CovenantLane.Confirmed
                || !IsInScope(snapshot, candidate)
                || candidate.Integrity != CovenantSnapshotCandidateIntegrity.Verified
                || !HasSupportedPolicies(candidate))
            {
                continue;
            }

            if (candidate.Scope == CovenantScope.Global
                && campaignConfirmedByKey.ContainsKey(candidate.NormalizedKey.Value))
            {
                continue;
            }

            result.Add(candidate.NormalizedKey.Value);
        }

        return result;
    }

    private static CovenantPlanCandidateDecision Decide(
        CovenantTurnSnapshot snapshot,
        CovenantSnapshotCandidate candidate,
        IReadOnlyDictionary<string, CovenantSnapshotCandidate> campaignConfirmedByKey,
        IReadOnlySet<string> effectiveConfirmedKeys)
    {
        CovenantPlacement placement = Placement(candidate);

        if (!IsInScope(snapshot, candidate))
        {
            return new CovenantPlanCandidateDecision(
                candidate,
                CovenantPlanDecision.Invalid,
                null,
                placement);
        }

        if (candidate.Lane == CovenantLane.Confirmed)
        {
            if (candidate.Scope == CovenantScope.Global
                && campaignConfirmedByKey.TryGetValue(
                    candidate.NormalizedKey.Value,
                    out CovenantSnapshotCandidate? shadowingCandidate))
            {
                return new CovenantPlanCandidateDecision(
                    candidate,
                    CovenantPlanDecision.Shadowed,
                    shadowingCandidate.VersionId,
                    placement);
            }

            return new CovenantPlanCandidateDecision(
                candidate,
                CovenantPlanDecision.EligibleConfirmed,
                null,
                placement);
        }

        CovenantPlanDecision proposedDecision = candidate.Integrity switch
        {
            CovenantSnapshotCandidateIntegrity.Invalid => CovenantPlanDecision.Invalid,
            CovenantSnapshotCandidateIntegrity.Quarantined => CovenantPlanDecision.Quarantined,
            _ when !HasSupportedPolicies(candidate) => CovenantPlanDecision.Quarantined,
            _ when effectiveConfirmedKeys.Contains(candidate.NormalizedKey.Value) =>
                CovenantPlanDecision.ReviewOnly,
            _ => CovenantPlanDecision.EligibleProposed
        };

        return new CovenantPlanCandidateDecision(
            candidate,
            proposedDecision,
            null,
            placement);
    }

    private static bool IsInScope(
        CovenantTurnSnapshot snapshot,
        CovenantSnapshotCandidate candidate) =>
        candidate.Scope == CovenantScope.Global
        || snapshot.CanonicalCampaignId is { } campaignId && candidate.CampaignId == campaignId;

    private static bool HasSupportedPolicies(CovenantSnapshotCandidate candidate) =>
        candidate.CompilerPolicy == CovenantCompiler.CompilerPolicyVersion
        && candidate.RendererPolicy == CovenantCompiler.RendererPolicyVersion;

    private static CovenantPlacement Placement(CovenantSnapshotCandidate candidate) =>
        candidate.Scope switch
        {
            CovenantScope.Global => CovenantPlacement.GlobalConfirmed,
            CovenantScope.Campaign when candidate.Lane == CovenantLane.Confirmed =>
                CovenantPlacement.CampaignConfirmed,
            CovenantScope.Campaign when candidate.Lane == CovenantLane.Proposed =>
                CovenantPlacement.CampaignProposed,
            _ => throw new ArgumentException("The Covenant candidate has no valid placement.", nameof(candidate))
        };

    private static Result IntegrityFailure(string message) =>
        Result.Failure(new Error(ErrorCodes.Covenant.IntegrityFailure, message));

    private sealed class CandidateComparer : IComparer<CovenantSnapshotCandidate>
    {
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        public static CandidateComparer Instance { get; } = new();

        public int Compare(CovenantSnapshotCandidate? left, CovenantSnapshotCandidate? right)
        {
            int result = ((byte)Placement(left!)).CompareTo((byte)Placement(right!));

            if (result == 0)
            {
                result = CompareKeyUtf8(left!.NormalizedKey, right!.NormalizedKey);
            }

            if (result == 0)
            {
                result = CompareGuid(left!.EntryId, right!.EntryId);
            }

            return result;
        }

        private static int CompareKeyUtf8(CovenantKey left, CovenantKey right)
        {
            Span<byte> leftBytes = stackalloc byte[CovenantLimits.MaxKeyCharacters];
            Span<byte> rightBytes = stackalloc byte[CovenantLimits.MaxKeyCharacters];
            int leftLength = StrictUtf8.GetBytes(left.Value.AsSpan(), leftBytes);
            int rightLength = StrictUtf8.GetBytes(right.Value.AsSpan(), rightBytes);

            return leftBytes[..leftLength].SequenceCompareTo(rightBytes[..rightLength]);
        }

        private static int CompareGuid(Guid left, Guid right)
        {
            Span<byte> leftBytes = stackalloc byte[16];
            Span<byte> rightBytes = stackalloc byte[16];

            left.TryWriteBytes(leftBytes, bigEndian: true, out _);
            right.TryWriteBytes(rightBytes, bigEndian: true, out _);

            return leftBytes.SequenceCompareTo(rightBytes);
        }
    }
}
