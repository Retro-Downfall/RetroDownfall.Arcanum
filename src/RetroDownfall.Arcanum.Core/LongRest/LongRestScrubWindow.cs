namespace RetroDownfall.Arcanum.Core.LongRest;

/// <summary>
/// Which of a generation's leaves one step re-proves in full. The windows tile the leaves in identity order and a revision picks one, so
/// whatever no trigger saw is met within ceil(leaves / <see cref="Leaves"/>) revisions of a sweep that advances by one.
/// </summary>
public static class LongRestScrubWindow
{
    /// <summary>How many leaves one window holds.</summary>
    public const int Leaves = 32;

    /// <summary>The number of leaves to skip, in identity order, before the window a revision selects.</summary>
    public static long Offset(long leafCount, long revision)
    {
        if (leafCount <= 0 || revision < 0)
        {
            return 0;
        }

        long windows = (leafCount + Leaves - 1) / Leaves;

        return checked(revision % windows * Leaves);
    }
}
