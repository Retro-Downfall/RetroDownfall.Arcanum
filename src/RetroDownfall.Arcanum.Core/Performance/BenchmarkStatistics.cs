namespace RetroDownfall.Arcanum.Core.Performance;

/// <summary>
/// The deterministic pseudorandom source the comparative benchmark gate draws from.
/// </summary>
/// <remarks>
/// PCG32, pinned seed, and no reliance on the platform's own generator. A benchmark that shuffled
/// batches or resampled with <c>Random</c> would produce a different verdict on a rerun of the same
/// two revisions, and a gate whose answer moves when nothing moved is not a gate.
///
/// <para>Written out rather than taken from a package because the sequence is part of the published
/// contract: a future build that upgraded a dependency and silently changed the stream would
/// invalidate every recorded baseline without touching a single measurement.</para>
/// </remarks>
internal sealed class Pcg32
{

    private const ulong DefaultIncrement = 1442695040888963407UL;

    private const ulong Multiplier = 6364136223846793005UL;

    private ulong _state;

    private readonly ulong _increment;

    public Pcg32(ulong seed, ulong sequence = 0)
    {

        // The increment must be odd for the LCG to reach full period; the low bit is forced rather
        // than rejected so every sequence value is usable.
        _increment = (sequence << 1) | 1UL;

        _state = 0UL;

        _ = NextUInt32();

        _state += seed;

        _ = NextUInt32();

    }

    public uint NextUInt32()
    {

        ulong previous = _state;

        _state = unchecked((previous * Multiplier) + _increment);

        uint xorshifted = (uint)(((previous >> 18) ^ previous) >> 27);

        int rotation = (int)(previous >> 59);

        return (xorshifted >> rotation) | (xorshifted << ((-rotation) & 31));

    }

    /// <summary>Draws a value in <c>[0, exclusiveBound)</c> without modulo bias.</summary>
    /// <remarks>
    /// Rejection sampling rather than a modulo. With a bound that does not divide 2^32 evenly, a
    /// modulo would make the low indices marginally likelier — a bias small enough to look like noise
    /// and large enough to move a bootstrap interval.
    /// </remarks>
    public uint NextBelow(uint exclusiveBound)
    {

        ArgumentOutOfRangeException.ThrowIfZero(exclusiveBound);

        uint threshold = (uint)((0x1_0000_0000UL - exclusiveBound) % exclusiveBound);

        while (true)
        {

            uint drawn = NextUInt32();

            if (drawn >= threshold)
            {

                return drawn % exclusiveBound;

            }

        }

    }

}

/// <summary>
/// The one percentile definition every Covenant benchmark number uses.
/// </summary>
/// <remarks>
/// Nearest-rank over every sample, with nothing discarded. Interpolated percentiles invent a value no
/// run produced, and trimming outliers removes exactly the samples a latency gate exists to catch —
/// the slow tail is the measurement, not noise around it.
/// </remarks>
internal static class NearestRankPercentile
{

    /// <summary>
    /// The value at <paramref name="percentile"/> of an already-sorted ascending sample.
    /// </summary>
    /// <remarks>
    /// Rank is <c>ceil(p/100 * n)</c>, clamped into the sample. The caller sorts, because the
    /// comparative gate takes several percentiles from one sample and sorting per call would be the
    /// dominant cost of a 10,000-replicate bootstrap.
    /// </remarks>
    public static double OfSorted(IReadOnlyList<double> sortedAscending, double percentile)
    {

        ArgumentNullException.ThrowIfNull(sortedAscending);

        if (sortedAscending.Count == 0)
        {

            throw new ArgumentException("A percentile needs at least one sample.", nameof(sortedAscending));

        }

        if (!double.IsFinite(percentile) || percentile is < 0 or > 100)
        {

            throw new ArgumentOutOfRangeException(nameof(percentile));

        }

        int rank = (int)Math.Ceiling(percentile / 100d * sortedAscending.Count);

        return sortedAscending[Math.Clamp(rank - 1, 0, sortedAscending.Count - 1)];

    }

    /// <summary>Sorts a copy and takes one percentile from it.</summary>
    public static double Of(IReadOnlyList<double> samples, double percentile)
    {

        ArgumentNullException.ThrowIfNull(samples);

        double[] sorted = [.. samples];

        Array.Sort(sorted);

        return OfSorted(sorted, percentile);

    }

    /// <summary>The median absolute deviation, used to judge control noise.</summary>
    /// <remarks>
    /// Median-based rather than standard deviation because the control is measuring scaffolding: a
    /// single scheduling hiccup should widen the reported noise a little, not enough to fail a run
    /// that was otherwise quiet.
    /// </remarks>
    public static double MedianAbsoluteDeviation(IReadOnlyList<double> samples)
    {

        ArgumentNullException.ThrowIfNull(samples);

        double median = Of(samples, 50);

        double[] deviations = new double[samples.Count];

        for (int index = 0; index < samples.Count; index++)
        {

            deviations[index] = Math.Abs(samples[index] - median);

        }

        Array.Sort(deviations);

        return OfSorted(deviations, 50);

    }

}

/// <summary>
/// The comparative verdict: how this revision's p95 compares with the base revision's.
/// </summary>
internal sealed record BenchmarkRatioInterval(double ObservedRatio, double LowerBound, double UpperBound)
{

    /// <summary>The observed p95 ratio a comparison has to exceed before it can block a merge.</summary>
    /// <remarks>
    /// Named rather than written inline because the workload manifest restates it for a reader, and
    /// the ordinary suite asserts the restatement against this constant. A literal here would let the
    /// rule move to 1.20 while the manifest kept advertising 1.10 and every test stayed green.
    /// </remarks>
    public const double ObservedRatioThreshold = 1.10d;

    /// <summary>The bootstrap interval's lower bound a comparison has to exceed as well.</summary>
    public const double IntervalLowerBoundThreshold = 1.05d;

    /// <summary>
    /// Whether this comparison is a regression under the published rule.
    /// </summary>
    /// <remarks>
    /// Both conditions, deliberately. A single noisy run can push the observed ratio past the first
    /// threshold on its own; requiring the interval's lower bound to also clear the second means the
    /// comparison has to be confident as well as large before it blocks a merge. Absolute ceilings
    /// stay authoritative regardless of what this says.
    /// </remarks>
    public bool IsRegression =>
        ObservedRatio > ObservedRatioThreshold && LowerBound > IntervalLowerBoundThreshold;

}

/// <summary>
/// The paired bootstrap behind the comparative gate.
/// </summary>
/// <remarks>
/// Paired because the two revisions are co-run in randomized interleaved batches: comparing batch to
/// batch cancels the drift a machine accumulates over a long run, which comparing pooled samples
/// would leave in the answer.
/// </remarks>
internal static class BenchmarkComparison
{

    /// <summary>The pinned seed. ASCII <c>ARCANUMt</c>, so a reader can tell it was chosen, not found.</summary>
    public const ulong Seed = 0x415243414E554D74UL;

    public const int Replicates = 10_000;

    /// <summary>
    /// Resamples paired batches with replacement and reports the ratio interval.
    /// </summary>
    /// <remarks>
    /// Every sample inside a selected pair travels with it. Resampling individual measurements would
    /// break the pairing that makes the comparison meaningful, and would narrow the interval by
    /// pretending there are more independent observations than the run actually produced.
    /// </remarks>
    public static BenchmarkRatioInterval Compare(
        IReadOnlyList<IReadOnlyList<double>> baseBatches,
        IReadOnlyList<IReadOnlyList<double>> candidateBatches,
        double percentile = 95)
    {

        ArgumentNullException.ThrowIfNull(baseBatches);

        ArgumentNullException.ThrowIfNull(candidateBatches);

        if (baseBatches.Count == 0 || baseBatches.Count != candidateBatches.Count)
        {

            throw new ArgumentException(
                "A paired comparison needs the same non-zero number of batches on each side.",
                nameof(candidateBatches));

        }

        double observed = NearestRankPercentile.Of([.. candidateBatches.SelectMany(static batch => batch)], percentile)
            / NearestRankPercentile.Of([.. baseBatches.SelectMany(static batch => batch)], percentile);

        Pcg32 random = new(Seed);

        double[] ratios = new double[Replicates];

        List<double> baseSample = [];

        List<double> candidateSample = [];

        for (int replicate = 0; replicate < Replicates; replicate++)
        {

            baseSample.Clear();

            candidateSample.Clear();

            for (int pair = 0; pair < baseBatches.Count; pair++)
            {

                int chosen = (int)random.NextBelow((uint)baseBatches.Count);

                baseSample.AddRange(baseBatches[chosen]);

                candidateSample.AddRange(candidateBatches[chosen]);

            }

            ratios[replicate] =
                NearestRankPercentile.Of(candidateSample, percentile)
                / NearestRankPercentile.Of(baseSample, percentile);

        }

        Array.Sort(ratios);

        return new BenchmarkRatioInterval(
            observed,
            NearestRankPercentile.OfSorted(ratios, 2.5),
            NearestRankPercentile.OfSorted(ratios, 97.5));

    }

}

/// <summary>
/// Whether an allocation run's empty-harness control was quiet enough to subtract.
/// </summary>
/// <remarks>
/// The allocation numbers are paired differences against an empty operation driven through the same
/// measurement loop, invoked through the same delegate and accounted across the same boundaries. That
/// subtraction is only meaningful while the control itself is stable; a noisy control turns a
/// difference of two large numbers into a measurement of the noise. These bounds are the published
/// ones, and a run that breaches any of them is failed rather than reported with a caveat.
/// </remarks>
internal sealed record BenchmarkControlNoise(double SpreadBytes, double MedianAbsoluteDeviationBytes, double NegativeFraction)
{

    public const double MaximumSpreadBytes = 8 * 1024;

    public const double MaximumMedianAbsoluteDeviationBytes = 2 * 1024;

    public const double MaximumNegativeFraction = 0.01;

    public bool IsAcceptable =>
        SpreadBytes <= MaximumSpreadBytes
        && MedianAbsoluteDeviationBytes <= MaximumMedianAbsoluteDeviationBytes
        && NegativeFraction <= MaximumNegativeFraction;

    /// <summary>
    /// Measures the control and the paired corrections it produced.
    /// </summary>
    /// <remarks>
    /// Nothing is clamped. A negative correction means the measured operation allocated less than the
    /// harness around it, which is evidence the pairing is not working — recording it as zero would
    /// hide exactly the signal this check exists to read.
    /// </remarks>
    public static BenchmarkControlNoise Measure(
        IReadOnlyList<double> controlSamples,
        IReadOnlyList<double> pairedCorrections)
    {

        ArgumentNullException.ThrowIfNull(controlSamples);

        ArgumentNullException.ThrowIfNull(pairedCorrections);

        double spread = NearestRankPercentile.Of(controlSamples, 95)
            - NearestRankPercentile.Of(controlSamples, 5);

        int negative = 0;

        for (int index = 0; index < pairedCorrections.Count; index++)
        {

            if (pairedCorrections[index] < 0)
            {

                negative++;

            }

        }

        return new BenchmarkControlNoise(
            spread,
            NearestRankPercentile.MedianAbsoluteDeviation(controlSamples),
            pairedCorrections.Count == 0 ? 0 : (double)negative / pairedCorrections.Count);

    }

}
