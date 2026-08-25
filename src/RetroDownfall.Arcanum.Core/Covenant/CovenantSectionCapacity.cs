namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// How large one rendered Covenant Section is, and how large it is allowed to be.
/// </summary>
/// <remarks>
/// Two places have to agree about this arithmetic and cannot be allowed to drift. The renderer
/// refuses to emit a Section over its placement bound, and the quota guard refuses the write that
/// would assemble one. A guard carrying its own copy of the formula would either refuse writes that
/// would have fitted, or accept a write that renders one byte over and breaks every turn afterwards
/// -- installation-wide, because the ceiling is a property of the Section and not of the entry.
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

}
