using RetroDownfall.Arcanum.Core.Performance;

namespace RetroDownfall.Arcanum.Tests.Benchmarks;

/// <summary>
/// The arithmetic every Covenant benchmark verdict rests on.
/// </summary>
/// <remarks>
/// Deliberately outside the <c>Performance</c> namespace and its <c>Perf</c> category. That namespace
/// holds wall-clock assertions and the coverage run excludes it, which is exactly the wrong treatment
/// for these: none of this measures anything, so it is deterministic and has to run on every lane.
/// The wall-clock gates this arithmetic serves live only in the dedicated benchmark command.
/// </remarks>
public sealed class BenchmarkStatisticsTests
{

    /// <summary>
    /// The published PCG32 reference stream for <c>initstate=42, initseq=54</c>.
    /// </summary>
    /// <remarks>
    /// Taken from the reference implementation rather than recorded from this one. A vector captured
    /// from our own output would pass forever, including after a change that silently moved the
    /// stream — and the stream is part of the contract, because every recorded baseline was produced
    /// under it.
    /// </remarks>
    private static readonly uint[] ReferenceStream =
    [
        0xa15c02b7, 0x7b47f409, 0xba1d3330, 0x83d2f293, 0xbfa4784b, 0xcbed606e,
    ];

    [Fact]
    public void The_generator_reproduces_the_published_reference_stream()
    {

        Pcg32 random = new(42, 54);

        uint[] produced = [.. Enumerable.Range(0, ReferenceStream.Length).Select(_ => random.NextUInt32())];

        Assert.Equal(ReferenceStream, produced);

    }

    [Fact]
    public void One_seed_produces_one_stream()
    {

        Pcg32 first = new(BenchmarkComparison.Seed);

        Pcg32 second = new(BenchmarkComparison.Seed);

        Assert.Equal(
            Enumerable.Range(0, 32).Select(_ => first.NextUInt32()),
            Enumerable.Range(0, 32).Select(_ => second.NextUInt32()));

    }

    [Fact]
    public void A_bounded_draw_stays_in_range_and_reaches_both_ends()
    {

        Pcg32 random = new(BenchmarkComparison.Seed);

        HashSet<uint> seen = [];

        for (int draw = 0; draw < 4_000; draw++)
        {

            uint value = random.NextBelow(7);

            Assert.InRange(value, 0u, 6u);

            _ = seen.Add(value);

        }

        // A bound that does not divide 2^32 evenly is exactly where a modulo would bias the low
        // indices. Every index being reachable is the cheap half of that check; the distribution test
        // below is the other half.
        Assert.Equal(7, seen.Count);

    }

    [Fact]
    public void A_bounded_draw_does_not_favour_the_low_indices()
    {

        Pcg32 random = new(BenchmarkComparison.Seed);

        int[] counts = new int[7];

        const int Draws = 700_000;

        for (int draw = 0; draw < Draws; draw++)
        {

            counts[random.NextBelow(7)]++;

        }

        // Modulo bias over this bound would show as a systematic excess in the first few buckets.
        // A one percent band is far tighter than that bias and far looser than sampling noise.
        foreach (int count in counts)
        {

            Assert.InRange(count, Draws / 7 * 0.99, Draws / 7 * 1.01);

        }

    }

    [Theory]

    [InlineData(0, 1)]

    [InlineData(50, 5)]

    [InlineData(95, 10)]

    [InlineData(100, 10)]

    public void A_percentile_is_the_nearest_rank_over_every_sample(double percentile, double expected)
    {

        double[] samples = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

        Assert.Equal(expected, NearestRankPercentile.Of(samples, percentile));

    }

    [Fact]
    public void A_percentile_never_invents_a_value_no_sample_produced()
    {

        double[] samples = [1, 100];

        // An interpolating definition would answer 50.5 here — a number no run produced. Nearest rank
        // answers 1, the sample at rank ceil(0.5 x 2) = 1, which is what a latency ceiling is meant to
        // be compared against.
        Assert.Equal(1, NearestRankPercentile.Of(samples, 50));

        // And the upper percentile is the other real sample, never a blend of the two.
        Assert.Equal(100, NearestRankPercentile.Of(samples, 51));

    }

    [Fact]
    public void The_slow_tail_is_kept_rather_than_trimmed()
    {

        double[] quiet = [1, 1, 1, 1, 1, 1, 1, 1, 1, 1];

        double[] withOutlier = [1, 1, 1, 1, 1, 1, 1, 1, 1, 900];

        // The outlier is the measurement, not noise around it: a p95 that discarded it would report
        // the same number for a run that stalled once as for a run that never did.
        Assert.Equal(1, NearestRankPercentile.Of(quiet, 95));

        Assert.Equal(900, NearestRankPercentile.Of(withOutlier, 95));

    }

    [Fact]
    public void An_empty_sample_has_no_percentile()
    {

        Assert.Throws<ArgumentException>(() => NearestRankPercentile.Of([], 95));

    }

    [Fact]
    public void Identical_revisions_compare_at_a_ratio_of_one()
    {

        IReadOnlyList<double>[] batches = Batches(10, 10, static index => 100 + (index % 5));

        BenchmarkRatioInterval interval = BenchmarkComparison.Compare(batches, batches);

        Assert.Equal(1, interval.ObservedRatio);

        Assert.False(interval.IsRegression);

        // The pairing makes this exact: every replicate draws the same batch on both sides.
        Assert.Equal(1, interval.LowerBound);

        Assert.Equal(1, interval.UpperBound);

    }

    [Fact]
    public void A_uniformly_slower_candidate_is_a_regression()
    {

        IReadOnlyList<double>[] baseline = Batches(10, 10, static index => 100 + (index % 5));

        IReadOnlyList<double>[] candidate = Batches(10, 10, static index => (100 + (index % 5)) * 1.5);

        BenchmarkRatioInterval interval = BenchmarkComparison.Compare(baseline, candidate);

        Assert.True(interval.ObservedRatio > 1.4);

        Assert.True(interval.IsRegression);

    }

    [Fact]
    public void A_large_but_unconfident_difference_is_not_a_regression()
    {

        // One batch in ten is far slower and the rest match. The observed ratio clears 1.10, but a
        // resample that misses that batch does not — so the interval's lower bound stays under 1.05
        // and the rule declines to block a merge on one noisy batch.
        IReadOnlyList<double>[] baseline = Batches(10, 10, static index => 100);

        IReadOnlyList<double>[] candidate =
        [
            .. Enumerable.Range(0, 10).Select(batch =>
                (IReadOnlyList<double>)[.. Enumerable.Repeat(batch == 3 ? 400d : 100d, 10)]),
        ];

        BenchmarkRatioInterval interval = BenchmarkComparison.Compare(baseline, candidate);

        Assert.True(interval.ObservedRatio > 1.10, $"observed {interval.ObservedRatio}");

        Assert.False(interval.IsRegression);

    }

    [Fact]
    public void The_comparison_is_reproducible_across_runs()
    {

        IReadOnlyList<double>[] baseline = Batches(10, 10, static index => 100 + (index % 7));

        IReadOnlyList<double>[] candidate = Batches(10, 10, static index => 108 + (index % 7));

        BenchmarkRatioInterval first = BenchmarkComparison.Compare(baseline, candidate);

        BenchmarkRatioInterval second = BenchmarkComparison.Compare(baseline, candidate);

        // A gate whose answer moves when nothing moved is not a gate.
        Assert.Equal(first, second);

    }

    [Fact]
    public void Mismatched_batch_counts_cannot_be_paired()
    {

        Assert.Throws<ArgumentException>(() =>
            BenchmarkComparison.Compare(Batches(3, 4, static _ => 1), Batches(2, 4, static _ => 1)));

    }

    [Fact]
    public void A_quiet_control_is_acceptable()
    {

        BenchmarkControlNoise noise = BenchmarkControlNoise.Measure(
            [.. Enumerable.Range(0, 100).Select(index => 1000d + (index % 3))],
            [.. Enumerable.Repeat(4096d, 100)]);

        Assert.True(noise.IsAcceptable);

    }

    [Fact]
    public void A_control_that_swings_more_than_eight_kibibytes_fails_the_run()
    {

        BenchmarkControlNoise noise = BenchmarkControlNoise.Measure(
            [.. Enumerable.Range(0, 100).Select(index => index * 400d)],
            [.. Enumerable.Repeat(4096d, 100)]);

        Assert.True(noise.SpreadBytes > BenchmarkControlNoise.MaximumSpreadBytes);

        Assert.False(noise.IsAcceptable);

    }

    /// <summary>
    /// A control that hitches between three levels fails on deviation alone, inside the spread bound.
    /// </summary>
    /// <remarks>
    /// The three bounds are a conjunction and every other case here breaches spread and deviation
    /// together or leaves deviation at zero, so deleting the deviation conjunct changed no verdict.
    /// A control whose whole 5th-to-95th range fits inside 8 KiB while a third of its samples sit a
    /// full 3 KiB from the median is exactly the shape the deviation bound exists for: the subtraction
    /// is against the median, so a run this unsteady measures its own scheduling rather than the
    /// operation.
    /// </remarks>
    [Fact]
    public void A_control_that_hitches_between_levels_fails_on_deviation_alone()
    {

        double[] control =
        [
            .. Enumerable.Repeat(0d, 34),
            .. Enumerable.Repeat(3000d, 33),
            .. Enumerable.Repeat(6000d, 33),
        ];

        BenchmarkControlNoise noise = BenchmarkControlNoise.Measure(
            control,
            [.. Enumerable.Repeat(4096d, 100)]);

        Assert.True(
            noise.SpreadBytes <= BenchmarkControlNoise.MaximumSpreadBytes,
            $"Spread of {noise.SpreadBytes} must stay inside its bound, or the spread term carries this case.");

        Assert.Equal(0, noise.NegativeFraction);

        Assert.True(
            noise.MedianAbsoluteDeviationBytes > BenchmarkControlNoise.MaximumMedianAbsoluteDeviationBytes,
            $"Deviation of {noise.MedianAbsoluteDeviationBytes} must breach its bound.");

        Assert.False(noise.IsAcceptable);

    }

    [Fact]
    public void More_than_one_percent_negative_corrections_fails_the_run()
    {

        // A negative correction means the measured operation allocated less than the harness around
        // it, which is evidence the pairing is not working rather than a small number to clamp.
        double[] corrections = [.. Enumerable.Repeat(4096d, 97), .. Enumerable.Repeat(-1d, 3)];

        BenchmarkControlNoise noise = BenchmarkControlNoise.Measure(
            [.. Enumerable.Repeat(1000d, 100)],
            corrections);

        Assert.Equal(0.03, noise.NegativeFraction, 3);

        Assert.False(noise.IsAcceptable);

    }

    [Fact]
    public void Exactly_one_percent_negative_corrections_still_passes()
    {

        double[] corrections = [.. Enumerable.Repeat(4096d, 99), -1d];

        BenchmarkControlNoise noise = BenchmarkControlNoise.Measure(
            [.. Enumerable.Repeat(1000d, 100)],
            corrections);

        Assert.True(noise.IsAcceptable);

    }

    private static IReadOnlyList<double>[] Batches(int batches, int size, Func<int, double> value) =>
        [
            .. Enumerable.Range(0, batches).Select(_ =>
                (IReadOnlyList<double>)[.. Enumerable.Range(0, size).Select(value)]),
        ];

}
