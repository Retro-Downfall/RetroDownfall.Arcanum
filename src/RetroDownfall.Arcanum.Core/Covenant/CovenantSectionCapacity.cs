using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// How large one rendered Covenant Section is, and how large it is allowed to be.
/// </summary>
/// <remarks>
/// Three places have to agree about this arithmetic and cannot be allowed to drift. The renderer
/// refuses to emit a Section over its placement bound, the quota guard refuses the write that would
/// assemble one, and the staging preflight refuses the proposal that would ask for that write. A
/// guard carrying its own copy of the formula would either refuse writes that would have fitted, or
/// accept a write that renders one byte over and breaks every turn afterwards -- installation-wide,
/// because the ceiling is a property of the Section and not of the entry.
///
/// <para>The two Confirmed placements are reached by the operator mutation surface. The
/// <see cref="CovenantPlacement.CampaignProposed"/> arm below -- its entry ceiling, its byte
/// ceiling, and the fence-and-framing arithmetic no other placement uses -- is reached by the agent
/// mutation factory, whose staged proposals become Proposed heads when the turn that staged them
/// commits its answer.</para>
/// </remarks>
public static class CovenantSectionCapacity
{

    /// <summary>The shortest fence a Proposed Section may use.</summary>
    public const int MinimumFenceLength = 3;

    /// <summary>The Proposed Section's framing bytes other than the two fences: <c>text\n</c> and the closing newline.</summary>
    public const int ProposedFramingBytesBesidesFences = 6;

    /// <summary>The most entries one placement's Section may carry.</summary>
    public static int MaximumEntries(CovenantPlacement placement) =>
        placement switch
        {

            CovenantPlacement.GlobalConfirmed => CovenantLimits.MaxGlobalConfirmedEntries,

            CovenantPlacement.CampaignConfirmed => CovenantLimits.MaxCampaignConfirmedEntries,

            CovenantPlacement.CampaignProposed => CovenantLimits.MaxCampaignProposedEntries,

            _ => throw new ArgumentOutOfRangeException(nameof(placement)),

        };

    /// <summary>The most rendered bytes one placement's Section may occupy.</summary>
    public static int MaximumRenderedBytes(CovenantPlacement placement) =>
        placement switch
        {

            CovenantPlacement.GlobalConfirmed => CovenantLimits.MaxGlobalConfirmedRenderedBytes,

            CovenantPlacement.CampaignConfirmed => CovenantLimits.MaxCampaignConfirmedRenderedBytes,

            CovenantPlacement.CampaignProposed => CovenantLimits.MaxCampaignProposedRenderedBytes,

            _ => throw new ArgumentOutOfRangeException(nameof(placement)),

        };

    /// <summary>The placement a scope and lane render into.</summary>
    /// <remarks>
    /// Global Proposed is unrepresentable at the mutation boundary, so it has no placement to name.
    /// </remarks>
    public static CovenantPlacement Placement(CovenantScope scope, CovenantLane lane) =>
        (scope, lane) switch
        {

            (CovenantScope.Global, CovenantLane.Confirmed) => CovenantPlacement.GlobalConfirmed,

            (CovenantScope.Campaign, CovenantLane.Confirmed) => CovenantPlacement.CampaignConfirmed,

            (CovenantScope.Campaign, CovenantLane.Proposed) => CovenantPlacement.CampaignProposed,

            _ => throw new ArgumentOutOfRangeException(nameof(lane), "Global Covenant content has no Proposed placement."),

        };

    /// <summary>
    /// The bytes a Section of these fragments renders to.
    /// </summary>
    /// <param name="placement">The Section's placement, which decides whether it is fenced.</param>
    /// <param name="entryCount">How many entries the Section carries.</param>
    /// <param name="fragmentBytes">The summed length of the compiled fragments the Section carries.</param>
    /// <param name="longestRequiredFenceLength">
    /// The longest fence any one of those fragments requires. Ignored by the Confirmed placements,
    /// which concatenate their fragments and frame nothing.
    /// </param>
    public static long RenderedBytes(
        CovenantPlacement placement,
        long entryCount,
        long fragmentBytes,
        int longestRequiredFenceLength)
    {

        ArgumentOutOfRangeException.ThrowIfNegative(entryCount);

        ArgumentOutOfRangeException.ThrowIfNegative(fragmentBytes);

        // An empty Section renders to nothing at all, framing included, so a placement that holds no
        // entries can never be over its bound.
        if (entryCount == 0)
        {

            return 0;

        }

        if (placement is not CovenantPlacement.CampaignProposed)
        {

            return fragmentBytes;

        }

        int fence = Math.Max(MinimumFenceLength, longestRequiredFenceLength);

        return checked(fragmentBytes + (2L * fence) + ProposedFramingBytesBesidesFences);

    }

    /// <summary>
    /// Whether one prospective batch leaves its Section inside both of that Section's bounds.
    /// </summary>
    /// <remarks>
    /// The one place the two ceilings are compared, so that the staging preflight an agent's proposal
    /// runs and the write authority that publishes it cannot reach different answers about the same
    /// Section. They used to reach different answers by construction: nothing measured the Section
    /// before staging at all, so the tool told the model its proposal was recorded and the commit then
    /// refused the batch — and because a batch and the answer it accompanied publish together, the
    /// refusal discarded the operator's reply along with it. An operator whose Proposed lane was full
    /// paid for every answer they asked for and received none of them.
    ///
    /// <para>Pure, and takes the retained occupancy rather than reading it, because the preflight
    /// measures under a read lease and the authority measures inside its write transaction. Only the
    /// reading differs; the arithmetic must not.</para>
    /// </remarks>
    public static Error? Refusal(
        CovenantPlacement placement,
        CovenantSectionOccupancy retained,
        CovenantSectionDemand demand)
    {

        ArgumentNullException.ThrowIfNull(demand);

        long entries = checked(retained.Entries + demand.NewEntries);

        int maximumEntries = MaximumEntries(placement);

        if (entries > maximumEntries)
        {

            return new Error(
                ErrorCodes.Covenant.CapacityExceeded,
                $"This mutation would exceed the {maximumEntries}-entry bound on the {placement} Section.");

        }

        long rendered = RenderedBytes(
            placement,
            entries,
            checked(retained.FragmentBytes + demand.NewFragmentBytes),
            Math.Max(retained.LongestFenceLength, demand.RequiredFenceLength));

        int maximumBytes = MaximumRenderedBytes(placement);

        return rendered > maximumBytes
            ? new Error(
                ErrorCodes.Covenant.CapacityExceeded,
                $"This mutation would render the {placement} Section at {rendered} bytes, past its {maximumBytes}-byte bound.")
            : null;

    }

    /// <summary>
    /// What one batch of intents would leave in each rendered Section it touches.
    /// </summary>
    /// <remarks>
    /// One row per lane, because the two lanes render into different Sections with different bounds.
    /// A retirement contributes no entry and no bytes but still names its key, so the reader that
    /// consumes this subtracts what that key occupies today: retiring is how an installation gets back
    /// under a Section bound, and a retirement charged as an addition could never do that.
    ///
    /// <para>Shared by the write authority and by the staging preflight rather than copied into each.
    /// A preflight that built its demand differently from the authority's would be measuring a batch
    /// the authority never sees, which is the same disagreement <see cref="Refusal"/> exists to
    /// prevent, one step earlier.</para>
    /// </remarks>
    public static ImmutableArray<CovenantSectionDemand> Demands(IEnumerable<CovenantMutationIntent> intents)
    {

        ArgumentNullException.ThrowIfNull(intents);

        Dictionary<CovenantLane, (HashSet<string> Keys, long Entries, long Bytes, int Fence)> lanes = [];

        foreach (CovenantMutationIntent intent in intents)
        {

            CovenantLane lane = intent.Target.Lane;

            if (!lanes.TryGetValue(lane, out (HashSet<string> Keys, long Entries, long Bytes, int Fence) row))
            {

                row = ([], 0, 0, 0);

            }

            _ = row.Keys.Add(intent.Target.NormalizedKey.Value);

            if (intent.Operation == CovenantOperation.Set && intent.Artifact is { } artifact)
            {

                row.Entries = checked(row.Entries + 1);

                row.Bytes = checked(row.Bytes + artifact.CompiledByteCost);

                row.Fence = Math.Max(row.Fence, artifact.RequiredFenceLength);

            }

            lanes[lane] = row;

        }

        return
        [
            .. lanes.Select(static lane => new CovenantSectionDemand(
                lane.Key,
                [.. lane.Value.Keys],
                lane.Value.Entries,
                lane.Value.Bytes,
                lane.Value.Fence)),
        ];

    }

}
