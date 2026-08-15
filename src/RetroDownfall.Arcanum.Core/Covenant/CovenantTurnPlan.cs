using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace RetroDownfall.Arcanum.Core.Covenant;

public sealed class CovenantPlanCandidateDecision
{
    private readonly PlanDecisionDigestInput _digestInput;

    internal CovenantPlanCandidateDecision(
        CovenantSnapshotCandidate candidate,
        CovenantPlanDecision decision,
        Guid? shadowingVersionId,
        CovenantPlacement placement)
    {
        Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        Decision = ValidateDecision(decision);
        ShadowingVersionId = ValidateShadow(decision, shadowingVersionId);
        Placement = ValidatePlacement(candidate, placement);
        ByteCost = candidate.CompiledBytes;
        _digestInput = new PlanDecisionDigestInput(
            candidate.EntryId,
            candidate.VersionId,
            Decision,
            ShadowingVersionId,
            Placement,
            candidate.FragmentDigest,
            ByteCost);
    }

    public CovenantSnapshotCandidate Candidate { get; }

    public CovenantPlanDecision Decision { get; }

    public Guid? ShadowingVersionId { get; }

    public CovenantPlacement Placement { get; }

    public uint ByteCost { get; }

    public PlanDecisionDigestInput ToDigestInput() =>
        _digestInput;

    private static CovenantPlanDecision ValidateDecision(CovenantPlanDecision decision) =>
        decision is >= CovenantPlanDecision.EligibleConfirmed and <= CovenantPlanDecision.Invalid
            ? decision
            : throw new ArgumentOutOfRangeException(nameof(decision));

    private static Guid? ValidateShadow(CovenantPlanDecision decision, Guid? shadowingVersionId)
    {
        if ((decision == CovenantPlanDecision.Shadowed) != shadowingVersionId.HasValue)
        {
            throw new ArgumentException("Only a shadowed Plan decision carries the shadowing version identity.", nameof(shadowingVersionId));
        }

        if (shadowingVersionId is { } present)
        {
            CovenantValidation.RequireNonEmpty(present, nameof(shadowingVersionId));
        }

        return shadowingVersionId;
    }

    private static CovenantPlacement ValidatePlacement(
        CovenantSnapshotCandidate candidate,
        CovenantPlacement placement)
    {
        CovenantPlacement expected = candidate.Scope switch
        {
            CovenantScope.Global => CovenantPlacement.GlobalConfirmed,
            CovenantScope.Campaign when candidate.Lane == CovenantLane.Confirmed =>
                CovenantPlacement.CampaignConfirmed,
            CovenantScope.Campaign when candidate.Lane == CovenantLane.Proposed =>
                CovenantPlacement.CampaignProposed,
            _ => throw new ArgumentException("The candidate has no valid Plan placement.", nameof(candidate))
        };

        if (placement != expected)
        {
            throw new ArgumentException("The Plan placement does not match the candidate scope and lane.", nameof(placement));
        }

        return placement;
    }
}

public sealed class CovenantTurnSection
{
    private readonly SectionDigestInput _digestInput;

    private CovenantTurnSection(
        CovenantPlacement placement,
        CovenantPlanCandidateDecision[] ownedCandidates,
        byte[] ownedRenderedBytes)
    {
        Placement = placement;
        Candidates = [.. ownedCandidates];
        RenderedBytes = ImmutableCollectionsMarshal.AsImmutableArray(ownedRenderedBytes);
        _digestInput = new SectionDigestInput(
            Placement,
            [.. Candidates.Select(static decision => new SectionItemDigestInput(
                decision.Candidate.NormalizedKey,
                decision.Candidate.EntryId,
                decision.Candidate.VersionId,
                decision.Candidate.Revision,
                decision.Candidate.FragmentDigest))],
            RenderedBytes);
        Digest = CovenantDigests.Section(_digestInput);
    }

    public CovenantPlacement Placement { get; }

    public ImmutableArray<CovenantPlanCandidateDecision> Candidates { get; }

    public ImmutableArray<byte> RenderedBytes { get; }

    public CovenantDigest Digest { get; }

    public SectionDigestInput ToDigestInput() =>
        _digestInput;

    internal static CovenantTurnSection Create(
        CovenantPlacement placement,
        IEnumerable<CovenantPlanCandidateDecision> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        CovenantPlanCandidateDecision[] frozen = candidates.ToArray();

        ValidateCandidates(placement, frozen);

        return new CovenantTurnSection(
            placement,
            frozen,
            CovenantSectionRenderer.Render(placement, frozen));
    }

    private static void ValidateCandidates(
        CovenantPlacement placement,
        CovenantPlanCandidateDecision[] candidates)
    {
        if (placement is not CovenantPlacement.GlobalConfirmed
            and not CovenantPlacement.CampaignConfirmed
            and not CovenantPlacement.CampaignProposed)
        {
            throw new ArgumentOutOfRangeException(nameof(placement));
        }

        foreach (CovenantPlanCandidateDecision decision in candidates)
        {
            ArgumentNullException.ThrowIfNull(decision, nameof(candidates));

            if (decision.Placement != placement
                || decision.Decision is not CovenantPlanDecision.EligibleConfirmed
                    and not CovenantPlanDecision.EligibleProposed)
            {
                throw new ArgumentException("A Section can contain only eligible decisions for its placement.", nameof(candidates));
            }
        }
    }
}

public sealed class CovenantTurnPlan
{
    public const uint LinkerPolicyVersion = 1;

    public const uint PlacementPolicyVersion = 1;

    private readonly PlanDigestInput _digestInput;

    internal CovenantTurnPlan(
        CovenantTurnSnapshot snapshot,
        IEnumerable<CovenantPlanCandidateDecision> decisions,
        CovenantTurnSection globalConfirmedSection,
        CovenantTurnSection campaignConfirmedSection,
        CovenantTurnSection campaignProposedSection)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        ArgumentNullException.ThrowIfNull(decisions);
        Decisions = [.. decisions];
        GlobalConfirmedSection = RequireSection(
            globalConfirmedSection,
            CovenantPlacement.GlobalConfirmed,
            nameof(globalConfirmedSection));
        CampaignConfirmedSection = RequireSection(
            campaignConfirmedSection,
            CovenantPlacement.CampaignConfirmed,
            nameof(campaignConfirmedSection));
        CampaignProposedSection = RequireSection(
            campaignProposedSection,
            CovenantPlacement.CampaignProposed,
            nameof(campaignProposedSection));

        ValidateDecisions();

        EligibleDecisions =
        [
            .. Decisions.Where(static decision =>
                decision.Decision is CovenantPlanDecision.EligibleConfirmed
                    or CovenantPlanDecision.EligibleProposed)
        ];

        ValidateSections();

        _digestInput = new PlanDigestInput(
            Snapshot.Digest,
            LinkerPolicyVersion,
            PlacementPolicyVersion,
            [.. Decisions.Select(static decision => decision.ToDigestInput())],
            GlobalConfirmedSection.Digest,
            CampaignConfirmedSection.Digest,
            CampaignProposedSection.Digest);
        Digest = CovenantDigests.Plan(_digestInput);
    }

    public CovenantTurnSnapshot Snapshot { get; }

    public uint LinkerPolicy => LinkerPolicyVersion;

    public uint PlacementPolicy => PlacementPolicyVersion;

    public ImmutableArray<CovenantPlanCandidateDecision> Decisions { get; }

    public ImmutableArray<CovenantPlanCandidateDecision> EligibleDecisions { get; }

    public CovenantTurnSection GlobalConfirmedSection { get; }

    public CovenantTurnSection CampaignConfirmedSection { get; }

    public CovenantTurnSection CampaignProposedSection { get; }

    public CovenantDigest Digest { get; }

    public PlanDigestInput ToDigestInput() =>
        _digestInput;

    private static CovenantTurnSection RequireSection(
        CovenantTurnSection section,
        CovenantPlacement placement,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(section, parameterName);

        if (section.Placement != placement)
        {
            throw new ArgumentException("The Plan Section has the wrong placement.", parameterName);
        }

        return section;
    }

    private void ValidateDecisions()
    {
        if (Decisions.Length != Snapshot.Candidates.Length)
        {
            throw new ArgumentException("The Plan must contain exactly one decision per snapshot candidate.", nameof(Decisions));
        }

        HashSet<CovenantSnapshotCandidate> remaining = new(
            Snapshot.Candidates,
            ReferenceEqualityComparer.Instance);

        foreach (CovenantPlanCandidateDecision decision in Decisions)
        {
            ArgumentNullException.ThrowIfNull(decision, nameof(Decisions));

            if (!remaining.Remove(decision.Candidate))
            {
                throw new ArgumentException("Plan decisions must reference each snapshot candidate exactly once.", nameof(Decisions));
            }
        }

        if (remaining.Count != 0)
        {
            throw new ArgumentException("Plan decisions must reference each snapshot candidate exactly once.", nameof(Decisions));
        }
    }

    private void ValidateSections()
    {
        ValidateSection(GlobalConfirmedSection);
        ValidateSection(CampaignConfirmedSection);
        ValidateSection(CampaignProposedSection);
    }

    private void ValidateSection(CovenantTurnSection section)
    {
        CovenantPlanCandidateDecision[] expected = EligibleDecisions
            .Where(decision => decision.Placement == section.Placement)
            .ToArray();

        if (!expected.AsSpan().SequenceEqual(section.Candidates.AsSpan()))
        {
            throw new ArgumentException("The Plan Section does not match its eligible decision subsequence.", nameof(section));
        }
    }
}

internal static class CovenantSectionRenderer
{
    public static byte[] Render(
        CovenantPlacement placement,
        ReadOnlySpan<CovenantPlanCandidateDecision> decisions)
    {
        int maximumBytes = placement switch
        {
            CovenantPlacement.GlobalConfirmed => CovenantLimits.MaxGlobalConfirmedRenderedBytes,
            CovenantPlacement.CampaignConfirmed => CovenantLimits.MaxCampaignConfirmedRenderedBytes,
            CovenantPlacement.CampaignProposed => CovenantLimits.MaxCampaignProposedRenderedBytes,
            _ => throw new ArgumentOutOfRangeException(nameof(placement))
        };

        if (decisions.IsEmpty)
        {
            return [];
        }

        return placement switch
        {
            CovenantPlacement.GlobalConfirmed or CovenantPlacement.CampaignConfirmed =>
                RenderConfirmed(decisions, maximumBytes),
            CovenantPlacement.CampaignProposed => RenderProposed(decisions, maximumBytes),
            _ => throw new ArgumentOutOfRangeException(nameof(placement))
        };
    }

    private static byte[] RenderConfirmed(
        ReadOnlySpan<CovenantPlanCandidateDecision> decisions,
        int maximumBytes)
    {
        int length = 0;

        foreach (CovenantPlanCandidateDecision decision in decisions)
        {
            length = checked(length + decision.Candidate.CompiledFragment.Length);
        }

        RequireWithinBound(length, maximumBytes);

        byte[] rendered = new byte[length];
        int offset = 0;

        foreach (CovenantPlanCandidateDecision decision in decisions)
        {
            ReadOnlySpan<byte> fragment = decision.Candidate.CompiledFragment.AsSpan();

            fragment.CopyTo(rendered.AsSpan(offset));
            offset += fragment.Length;
        }

        return rendered;
    }

    private static byte[] RenderProposed(
        ReadOnlySpan<CovenantPlanCandidateDecision> decisions,
        int maximumBytes)
    {
        int contentLength = 0;
        int longestBacktickRun = 0;

        foreach (CovenantPlanCandidateDecision decision in decisions)
        {
            ReadOnlySpan<byte> fragment = decision.Candidate.CompiledFragment.AsSpan();
            int currentRun = 0;

            contentLength = checked(contentLength + fragment.Length);

            foreach (byte value in fragment)
            {
                if (value == (byte)'`')
                {
                    currentRun++;
                    longestBacktickRun = Math.Max(longestBacktickRun, currentRun);
                }
                else
                {
                    currentRun = 0;
                }
            }
        }

        int fenceLength = Math.Max(3, checked(longestBacktickRun + 1));
        int totalLength = checked(fenceLength + "text\n"u8.Length + contentLength + fenceLength + 1);

        RequireWithinBound(totalLength, maximumBytes);

        byte[] rendered = new byte[totalLength];
        Span<byte> destination = rendered;
        int offset = 0;

        destination[..fenceLength].Fill((byte)'`');
        offset += fenceLength;
        "text\n"u8.CopyTo(destination[offset..]);
        offset += "text\n"u8.Length;

        foreach (CovenantPlanCandidateDecision decision in decisions)
        {
            ReadOnlySpan<byte> fragment = decision.Candidate.CompiledFragment.AsSpan();

            fragment.CopyTo(destination[offset..]);
            offset += fragment.Length;
        }

        destination.Slice(offset, fenceLength).Fill((byte)'`');
        offset += fenceLength;
        destination[offset] = (byte)'\n';

        return rendered;
    }

    private static void RequireWithinBound(int renderedBytes, int maximumBytes)
    {
        if (renderedBytes > maximumBytes)
        {
            throw new ArgumentException("The rendered Covenant Section exceeds its placement byte bound.");
        }
    }
}
